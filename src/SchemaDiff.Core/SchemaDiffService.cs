using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core;

/// <summary>
/// Tek bir kaynak/hedef çiftinin karşılaştırılması.
/// Birden fazla çifti paralel koşturmak için <see cref="Jobs.ComparisonRunner"/>.
/// </summary>
public static class SchemaDiffService
{
    public static async Task<DatabaseSnapshot> CaptureAsync(
        string connectionString,
        SnapshotOptions? options = null,
        ExtractionGate? gate = null,
        CancellationToken ct = default)
    {
        options ??= SnapshotOptions.Default;
        var extractor = new CatalogExtractor(gate);
        var (catalog, report) = await extractor.ExtractAsync(connectionString, ct);
        return SnapshotBuilder.Build(catalog, report, options);
    }

    /// <summary>İki tarafı paralel çeker — tek taraflı bekleme yok.</summary>
    public static async Task<CompareResult> CompareAsync(
        string sourceConnectionString,
        string targetConnectionString,
        SnapshotOptions? options = null,
        ExtractionGate? gate = null,
        CancellationToken ct = default)
    {
        var sourceTask = CaptureAsync(sourceConnectionString, options, gate, ct);
        var targetTask = CaptureAsync(targetConnectionString, options, gate, ct);
        await Task.WhenAll(sourceTask, targetTask);
        return SchemaComparer.Compare(await sourceTask, await targetTask);
    }
}
