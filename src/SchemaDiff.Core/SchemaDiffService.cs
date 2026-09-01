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
        CancellationToken ct = default,
        CompareProgress? progress = null)
    {
        options ??= SnapshotOptions.Default;
        var extractor = new CatalogExtractor(gate);
        var (catalog, report) = await extractor.ExtractAsync(connectionString, ct, progress);
        return SnapshotBuilder.Build(catalog, report, options);
    }

    /// <summary>
    /// İki tarafı paralel çeker — tek taraflı bekleme yok. İlerleme aşamaları temiz kalsın diye
    /// çıkarma (extraction) ve snapshot kurma (build) ayrı yürütülür: önce her iki taraf paralel
    /// çekilir (Extracting), sonra snapshot'lar kurulur (Building), en son karşılaştırılır (Comparing).
    /// </summary>
    public static async Task<CompareResult> CompareAsync(
        string sourceConnectionString,
        string targetConnectionString,
        SnapshotOptions? options = null,
        ExtractionGate? gate = null,
        CancellationToken ct = default,
        CompareProgress? progress = null)
    {
        options ??= SnapshotOptions.Default;
        progress ??= CompareProgress.None;

        var sourceExtractor = new CatalogExtractor(gate);
        var targetExtractor = new CatalogExtractor(gate);
        var sourceTask = sourceExtractor.ExtractAsync(sourceConnectionString, ct, progress);
        var targetTask = targetExtractor.ExtractAsync(targetConnectionString, ct, progress);
        await Task.WhenAll(sourceTask, targetTask);

        progress.EnterPhase(ComparePhase.Building);
        var (sourceCatalog, sourceReport) = await sourceTask;
        var (targetCatalog, targetReport) = await targetTask;
        // Kurma toplamını iki tarafın modül sayısından ÖNCEDEN belirle: aksi hâlde ikinci
        // tarafın kurulumu başlarken toplam büyür ve yüzde geri düşerdi.
        progress.AddBuildItems(sourceCatalog.Modules.Count + targetCatalog.Modules.Count);
        var source = SnapshotBuilder.Build(sourceCatalog, sourceReport, options, progress);
        var target = SnapshotBuilder.Build(targetCatalog, targetReport, options, progress);

        progress.EnterPhase(ComparePhase.Comparing);
        var result = SchemaComparer.Compare(source, target);
        progress.EnterPhase(ComparePhase.Done);
        return result;
    }
}
