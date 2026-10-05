using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SchemaDiff.Web;

/// <summary>
/// Üretilen dağıtım script'ini hedef veritabanında çalıştırır ve SSMS'e benzer mesajlar
/// (PRINT çıktısı, "(N rows affected)", ve "Msg … Level … State … Line …" hataları) üretir.
/// Script'i <c>GO</c> satırlarından batch'lere böler ve TEK bağlantıda sırayla çalıştırır —
/// böylece script'in kendi <c>BEGIN TRAN … COMMIT</c> transaction'ı batch'ler boyunca geçerli
/// kalır. Microsoft.Data.SqlClient <c>GO</c>'yu anlamaz; bölme bu yüzden burada yapılır.
/// </summary>
public static class ScriptExecutor
{
    // Kendi satırında duran GO (opsiyonel tekrar sayısı). String/yorum içindeki GO bölünmez
    // çünkü üretilen script'te GO daima kendi satırındadır.
    private static readonly Regex GoSplit = new(
        @"^[ \t]*GO[ \t]*(\d+)?[ \t]*(?:--[^\n]*)?$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Script'i GO ayracına göre batch'lere böler. Boş batch'ler atlanır; "GO N" N kez tekrarlar.</summary>
    public static List<string> SplitBatches(string sql)
    {
        // Satır sonlarını \n'e indir: GO regex'i (ve SQL Server) \r\n ile de sorunsuz çalışsın.
        sql = sql.Replace("\r\n", "\n").Replace("\r", "\n");
        var batches = new List<string>();
        var last = 0;
        foreach (Match m in GoSplit.Matches(sql))
        {
            var batch = sql[last..m.Index];
            if (!string.IsNullOrWhiteSpace(batch))
            {
                var count = m.Groups[1].Success && int.TryParse(m.Groups[1].Value, out var c) ? Math.Max(1, c) : 1;
                for (var i = 0; i < count; i++) batches.Add(batch);
            }
            last = m.Index + m.Length;
        }
        var tail = sql[last..];
        if (!string.IsNullOrWhiteSpace(tail)) batches.Add(tail);
        return batches;
    }

    public static async Task RunAsync(string connectionString, string sql, ExecuteRun run, CancellationToken ct)
    {
        var batches = SplitBatches(sql);
        run.SetTotal(batches.Count);
        if (batches.Count == 0)
        {
            run.Push(ExecuteMessage.Info("(Çalıştırılacak ifade yok — script boş.)"));
            run.Complete(true);
            return;
        }

        try
        {
            await using var conn = new SqlConnection(connectionString);

            // PRINT ve düşük-öncelikli (Class ≤ 10) sunucu mesajları buradan gelir — SSMS'in
            // "Messages" sekmesindeki satırların aynısı.
            conn.InfoMessage += (_, e) =>
            {
                foreach (SqlError err in e.Errors)
                    run.Push(ExecuteMessage.Server("info", err));
            };

            await conn.OpenAsync(ct);

            for (var i = 0; i < batches.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                await using var cmd = new SqlCommand(batches[i], conn) { CommandTimeout = 0 };  // SSMS gibi: süre sınırsız

                // SET NOCOUNT ON yoksa her ifadenin "(N rows affected)" satırı burada yakalanır.
                cmd.StatementCompleted += (_, e) =>
                {
                    if (e.RecordCount < 0) return;
                    run.Push(ExecuteMessage.Info(e.RecordCount == 1 ? "(1 row affected)" : $"({e.RecordCount} rows affected)"));
                };

                try
                {
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqlException ex)
                {
                    // Terminal hatalar: SSMS biçiminde (Msg/Level/State/Line) raporla, dur.
                    foreach (SqlError err in ex.Errors)
                        run.Push(ExecuteMessage.Server("error", err));
                    run.Push(ExecuteMessage.Fail(
                        "İşlem başarısız oldu. XACT_ABORT ile transaction geri alındı — veritabanı değişmedi."));
                    run.Complete(false);
                    return;
                }

                run.BatchDone();
            }

            run.Push(ExecuteMessage.Done("Commands completed successfully."));
            run.Complete(true);
        }
        catch (OperationCanceledException)
        {
            run.Push(ExecuteMessage.Fail("İşlem iptal edildi."));
            run.Complete(false);
        }
        catch (Exception ex)
        {
            run.Push(ExecuteMessage.Simple("error", ex.Message.Split('\n')[0].Trim()));
            run.Complete(false);
        }
    }
}

/// <summary>SSMS "Messages" satırı. Kind: info | rows | error | fail | done.</summary>
public sealed record ExecuteMessage(string Kind, string Text, int? Number, int? Level, int? State, int? Line)
{
    public static ExecuteMessage Info(string text) => new("info", text, null, null, null, null);
    public static ExecuteMessage Simple(string kind, string text) => new(kind, text, null, null, null, null);
    public static ExecuteMessage Fail(string text) => new("fail", text, null, null, null, null);
    public static ExecuteMessage Done(string text) => new("done", text, null, null, null, null);

    public static ExecuteMessage Server(string kind, SqlError err) => new(
        kind,
        err.Message,
        err.Number == 0 ? null : err.Number,
        err.Class,
        err.State,
        err.LineNumber > 0 ? err.LineNumber : null);
}

/// <summary>
/// Tek bir çalıştırma koşumu: mesajlar biriktirilir, SSE ile artımlı yayınlanır. İstemci
/// bağlantıyı kapatsa bile koşum arka planda tamamlanır (yarım deploy bırakmamak için).
/// </summary>
public sealed class ExecuteRun
{
    private readonly object _gate = new();   // net8 + net10 uyumlu
    private TaskCompletionSource _signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<ExecuteMessage> _messages = new();

    public int TotalBatches { get; private set; }
    public int DoneBatches { get; private set; }
    public bool Finished { get; private set; }
    public bool Success { get; private set; }

    public Task Changed { get { lock (_gate) return _signal.Task; } }

    public void SetTotal(int n) { lock (_gate) { TotalBatches = n; Signal(); } }

    /// <summary><paramref name="from"/> indeksinden itibaren yeni mesajları döndürür.</summary>
    public List<ExecuteMessage> MessagesFrom(int from)
    {
        lock (_gate) return from >= _messages.Count ? [] : _messages[from..];
    }

    public void Push(ExecuteMessage m) { lock (_gate) { _messages.Add(m); Signal(); } }
    public void BatchDone() { lock (_gate) { DoneBatches++; Signal(); } }
    public void Complete(bool ok) { lock (_gate) { Finished = true; Success = ok; Signal(); } }

    private void Signal()
    {
        var previous = _signal;
        _signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        previous.TrySetResult();
    }
}
