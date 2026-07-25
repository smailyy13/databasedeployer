using System.Diagnostics;
using System.Runtime.CompilerServices;
using SchemaDiff.Core.Extraction;

namespace SchemaDiff.Core.Jobs;

/// <summary>
/// Seçilen işleri paralel koşturur ve sonuçları BİTTİKÇE akıtır.
///
/// Task.WhenAll yerine Task.WhenEach: 7 işten biri 30 saniye sürüyorsa, diğer 6'sının
/// sonucunu 30 saniye bekletmenin anlamı yok. İlk sonuç ilk bitende ekrana düşer.
/// </summary>
public static class ComparisonRunner
{
    public static async IAsyncEnumerable<JobResult> RunAsync(
        IReadOnlyList<ComparisonJob> jobs,
        SnapshotOptions? options = null,
        int maxConcurrentQueries = 16,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (jobs.Count == 0) yield break;

        var gate = new ExtractionGate(maxConcurrentQueries);
        var tasks = jobs.Select(job => RunOneAsync(job, options, gate, ct)).ToList();

        try
        {
            await foreach (var completed in Task.WhenEach(tasks).WithCancellation(ct))
                yield return await completed;
        }
        finally
        {
            // Kapı, kendisini kullanan görevlerden önce kapatılmamalı — tüketici
            // döngüyü erken terkederse ObjectDisposedException alırız.
            try { await Task.WhenAll(tasks); }
            catch (OperationCanceledException) { /* iptal edildi; kapatmaya devam */ }
            gate.Dispose();
        }
    }

    /// <summary>Tüm işleri koşturur ve seçim sırasına göre sıralı liste döner.</summary>
    public static async Task<IReadOnlyList<JobResult>> RunAllAsync(
        IReadOnlyList<ComparisonJob> jobs,
        SnapshotOptions? options = null,
        int maxConcurrentQueries = 16,
        CancellationToken ct = default)
    {
        var results = new List<JobResult>(jobs.Count);
        await foreach (var result in RunAsync(jobs, options, maxConcurrentQueries, ct))
            results.Add(result);

        return Order(jobs, results);
    }

    /// <summary>
    /// Akış tamamlanma sırasındadır; rapor ise seçim sırasında olmalı.
    /// Katman sırası çoğu zaman anlamlıdır (deployment sırası gibi).
    /// </summary>
    public static IReadOnlyList<JobResult> Order(
        IReadOnlyList<ComparisonJob> jobs, IEnumerable<JobResult> results)
    {
        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < jobs.Count; i++) order[jobs[i].Name] = i;

        return results.OrderBy(r => order.GetValueOrDefault(r.Name, int.MaxValue)).ToList();
    }

    /// <summary>
    /// Bir işin başarısızlığı diğerlerini durdurmaz. Yedi katmanlı bir deployment'ta
    /// tek bir erişim sorunu yüzünden hiç sonuç alamamak kabul edilebilir değil.
    /// </summary>
    private static async Task<JobResult> RunOneAsync(
        ComparisonJob job, SnapshotOptions? options, ExtractionGate gate, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var comparison = await SchemaDiffService.CompareAsync(
                job.SourceConnectionString, job.TargetConnectionString, options, gate, ct);
            return new JobResult(job, comparison, null, stopwatch.Elapsed);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new JobResult(job, null, ex.Message.Split('\n')[0].Trim(), stopwatch.Elapsed);
        }
    }
}
