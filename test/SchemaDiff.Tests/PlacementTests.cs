using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Depolama yerleşimi: CREATE TABLE/INDEX sonuna "ON [filegroup]" / "ON [scheme]([kolon])"
/// eklenir ve yerleşim farkı kanonikte tespit edilir.
/// </summary>
public class PlacementTests
{
    private const int TId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        TId, id, name, "sys", "int", 4, 10, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    // DataSpaceId ile NONCLUSTERED index.
    private static IndexRow Idx(int dataSpaceId) =>
        new(TId, 2, "IX_A", "NONCLUSTERED", false, false, false, 0, false, false, null, null, true, true, dataSpaceId);

    private static IndexColumnRow Key() => new(TId, 2, 2, 2, 1, false, false);

    // Varsayılan data_space'ler: 1=PRIMARY, 2=IDXFG (filegroup), 3=ps_dt (partition scheme).
    private static List<DataSpaceRow> Spaces() =>
        [new(1, "PRIMARY", "FG"), new(2, "IDXFG", "FG"), new(3, "ps_dt", "PS")];

    private static CatalogSet Table(int tableSpace, int indexSpace, bool scheme = false) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(TId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "A")],
        Indexes = [Idx(indexSpace)],
        IndexColumns = [Key()],
        DataSpaces = Spaces(),
        TablePlacements = [new TablePlacementRow(TId, 0, tableSpace)],
        PartitionColumns = scheme ? [new PartitionColumnRow(TId, 0, "A")] : [],
    };

    private static CatalogSet Empty() => new() { DatabaseName = "test", ServerName = "TESTSRV" };

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(CatalogSet source) =>
        TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(source), Build(Empty())), null, TableScriptOptions.Default).Sql;

    [Fact]
    public void Table_and_index_filegroup_are_written()
    {
        var sql = Script(Table(tableSpace: 2, indexSpace: 2));   // ikisi de IDXFG
        Assert.Contains(") ON [IDXFG];", sql);                   // CREATE TABLE
        Assert.Contains("ON [IDXFG];", sql);                     // CREATE INDEX
    }

    [Fact]
    public void Primary_placement_is_written_explicitly()
    {
        Assert.Contains(") ON [PRIMARY];", Script(Table(tableSpace: 1, indexSpace: 1)));
    }

    [Fact]
    public void Partition_scheme_carries_the_column()
    {
        // Tablo partition scheme (id 3) üzerinde → "ON [ps_dt]([A])".
        Assert.Contains(") ON [ps_dt]([A]);", Script(Table(tableSpace: 3, indexSpace: 2, scheme: true)));
    }

    [Fact]
    public void Placement_difference_is_detected()
    {
        // Aynı tablo, yalnız yerleşim farkı (IDXFG ↔ PRIMARY) → fark.
        var onFg = Build(Table(tableSpace: 2, indexSpace: 2));
        var onPrimary = Build(Table(tableSpace: 1, indexSpace: 1));
        Assert.NotEmpty(SchemaComparer.Compare(onFg, onPrimary).Differences);
    }

    [Fact]
    public void Same_placement_is_equal()
    {
        var a = Build(Table(tableSpace: 2, indexSpace: 2));
        var b = Build(Table(tableSpace: 2, indexSpace: 2));
        Assert.Empty(SchemaComparer.Compare(a, b).Differences);
    }
}
