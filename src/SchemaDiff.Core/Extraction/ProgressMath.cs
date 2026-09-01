namespace SchemaDiff.Core.Extraction;

/// <summary>
/// Ham ilerlemeyi (aşama + sorgu/modül sayıları) toplam yüzdeye ve kalan süre tahminine çevirir.
/// Saf fonksiyon: UI'dan bağımsız, deterministik ve test edilebilir olsun diye Core'da durur.
///
/// İki pahalı aşama vardır ve hangisinin baskın olduğu ortama göre değişir: uzak (banka ağı)
/// veritabanında çıkarma sorguları, yerelde modül parse'ı. Bu yüzden her ikisine de geniş,
/// KENDİ ilerlemesiyle animasyonlu bir bant verilir; sabit sıçrama bırakılmaz.
/// </summary>
public static class ProgressMath
{
    public const int ExtractStart = 5;   // bağlantı/preflight bitti
    public const int ExtractEnd = 62;    // tüm çıkarma sorguları bitti
    public const int BuildEnd = 96;      // snapshot'lar kuruldu (modül normalizasyonu)
    public const int CompareEnd = 98;    // karşılaştırma bitti (kalanı DTO map)

    public static int Percent(ProgressSnapshot s)
    {
        var percent = s.Phase switch
        {
            ComparePhase.Connecting => 2,
            ComparePhase.Extracting => s.QueriesTotal <= 0
                ? ExtractStart
                : ExtractStart + (int)((ExtractEnd - ExtractStart) * (double)s.QueriesDone / s.QueriesTotal),
            ComparePhase.Building => s.BuildTotal <= 0
                ? BuildEnd
                : ExtractEnd + (int)((BuildEnd - ExtractEnd) * (double)s.BuildDone / s.BuildTotal),
            ComparePhase.Comparing => CompareEnd,
            _ => 100,
        };
        return Math.Clamp(percent, 0, 100);
    }

    /// <summary>
    /// Geçen süreden doğrusal ekstrapolasyonla kalan saniye. Yüzde küçükken (gürültülü) ya da
    /// iş bittiğinde tahmin verilmez — erken/anlamsız sayı yanıltıcıdır.
    /// </summary>
    public static int? EtaSeconds(int percent, TimeSpan elapsed)
    {
        if (percent is < 10 or >= 100 || elapsed.TotalSeconds < 1.0) return null;
        var remaining = elapsed.TotalSeconds * (100 - percent) / percent;
        return (int)Math.Ceiling(remaining);
    }

    /// <summary>Tek adımda yüzde + ETA.</summary>
    public static (int Percent, int? EtaSeconds) Compute(ProgressSnapshot s, TimeSpan elapsed)
    {
        var percent = Percent(s);
        return (percent, EtaSeconds(percent, elapsed));
    }
}
