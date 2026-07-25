namespace SchemaDiff.Core.Extraction;

/// <summary>
/// Süreç genelinde eşzamanlı sorgu sayısını sınırlar.
/// Tek veritabanı karşılaştırması 18 sorgu × 2 taraf açar; 7 katmanlı bir EDW
/// deployment'ında bu 252 eşzamanlı bağlantı demektir — sunucuyu boğar.
/// Kapı, katmanları paralel koşarken bağlantı sayısını makul tutar.
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
