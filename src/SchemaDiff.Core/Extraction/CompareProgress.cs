namespace SchemaDiff.Core.Extraction;

/// <summary>Karşılaştırmanın kaba aşamaları. Sıra artan: geri gidilmez.</summary>
public enum ComparePhase
{
    Connecting = 0,
    Extracting = 1,
    Building = 2,
    Comparing = 3,
    Done = 4,
}

/// <summary>
/// O anki ilerlemenin anlık görüntüsü. Yüzde/ETA hesabı çağırana (Web) bırakılır.
/// İki ayrı iş boyutu vardır: çıkarma sorguları ve model kurmada normalize edilen modüller —
/// çünkü uzak (banka ağı) veritabanında baskın maliyet sorgular, yerelde ise modül parse'ıdır.
/// </summary>
public sealed record ProgressSnapshot(
    ComparePhase Phase, int QueriesDone, int QueriesTotal, int BuildDone, int BuildTotal);

/// <summary>
/// Karşılaştırma ilerlemesini toplar. İki taraf (kaynak/hedef) sorgularını PARALEL koşar,
/// bu yüzden tüm sayaçlar tek örnekte, kilitsiz (Interlocked) toplanır.
///
/// İlerlemenin baskın maliyeti çıkarma (extraction) sorgularıdır: her sorgu tamamlandığında
/// sayaç artar; toplam sorgu sayısı sorgular kuyruğa alınırken bilinir. Yüzde ve kalan süre
/// tahmini bu ham sayılardan Web katmanında türetilir — Core deterministik ve UI'dan bağımsız kalır.
/// </summary>
public sealed class CompareProgress
{
    private readonly Action<ProgressSnapshot>? _onChange;
    private int _queriesTotal;
    private int _queriesDone;
    private int _buildTotal;
    private int _buildDone;
    private int _phase; // ComparePhase; yalnız ileri (monotonik) güncellenir

    public CompareProgress(Action<ProgressSnapshot>? onChange = null) => _onChange = onChange;

    /// <summary>Hiç ilerleme raporlamayan boş nesne — progress istemeyen çağıranlar için.</summary>
    public static CompareProgress None { get; } = new();

    /// <summary>Kuyruğa alınan sorgu sayısını ekler (her iki taraf da kendi payını ekler).</summary>
    public void AddQueries(int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _queriesTotal, count);
        Notify();
    }

    /// <summary>Bir sorgu tamamlandı (başarılı ya da opsiyonel-başarısız fark etmez).</summary>
    public void QueryCompleted()
    {
        Interlocked.Increment(ref _queriesDone);
        Notify();
    }

    /// <summary>Model kurmada normalize edilecek modül sayısını ekler (her iki taraf birlikte).</summary>
    public void AddBuildItems(int count)
    {
        if (count <= 0) return;
        Interlocked.Add(ref _buildTotal, count);
        Notify();
    }

    /// <summary>Bir modül normalize edildi.</summary>
    public void BuildItemCompleted()
    {
        Interlocked.Increment(ref _buildDone);
        Notify();
    }

    /// <summary>Aşamayı ilerletir. Geri gitme yok: paralel taraflardan biri erken ilerlese bile
    /// en ileri aşama korunur.</summary>
    public void EnterPhase(ComparePhase phase)
    {
        var target = (int)phase;
        int current;
        do
        {
            current = Volatile.Read(ref _phase);
            if (target <= current) return;
        }
        while (Interlocked.CompareExchange(ref _phase, target, current) != current);
        Notify();
    }

    public ProgressSnapshot Current => new(
        (ComparePhase)Volatile.Read(ref _phase),
        Volatile.Read(ref _queriesDone),
        Volatile.Read(ref _queriesTotal),
        Volatile.Read(ref _buildDone),
        Volatile.Read(ref _buildTotal));

    private void Notify() => _onChange?.Invoke(Current);
}
