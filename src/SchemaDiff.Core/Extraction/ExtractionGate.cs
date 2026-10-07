namespace SchemaDiff.Core.Extraction;

/// <summary>
/// Kendisini PAYLAŞAN karşılaştırmalar arasında eşzamanlı sorgu sayısını sınırlar.
/// Tek veritabanı karşılaştırması 51 sorgu × 2 taraf açar; 7 katmanlı bir EDW
/// deployment'ında bu 714 eşzamanlı bağlantı demektir — sunucuyu boğar.
///
/// Kapının kapsamı ÇAĞIRANA bağlıdır ve iki arayüzde bilinçli olarak farklıdır:
///
/// <list type="bullet">
/// <item>CLI (<see cref="Jobs.ComparisonRunner"/>): bir koşumdaki TÜM işler tek kapıyı
/// paylaşır. <c>--jobs 1-7</c> ile 7 katman paralel koşarken toplam sorgu sayısı
/// <c>--max-queries</c> ile sınırlı kalır (varsayılan 16).</item>
/// <item>Web (<c>CompareService.Start</c>): her karşılaştırma oturumu KENDİ kapısını
/// açar. Olağan kullanım 7+ katmanı 7+ sekmede aynı anda karşılaştırmaktır ve amaç
/// hızdır; ortak bir kapı sekmelerin birbirini beklemesi demek olurdu. Bedeli,
/// eşzamanlı sorgu sayısının sekme sayısıyla çarpılmasıdır — 7 sekme × 16 = 112.
/// Sunucunun bunu kaldıramadığı ortamda sekme başına <c>MaxQueries</c> düşürülür.</item>
/// </list>
/// </summary>
public sealed class ExtractionGate(int maxConcurrent) : IDisposable
{
    public static readonly ExtractionGate Unbounded = new(int.MaxValue);

    private readonly SemaphoreSlim _semaphore = new(maxConcurrent, maxConcurrent);

    public int MaxConcurrent { get; } = maxConcurrent;

    public async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            return await action();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void Dispose() => _semaphore.Dispose();
}
