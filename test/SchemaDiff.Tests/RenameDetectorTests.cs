using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

public class RenameDetectorTests
{
    private static ColumnRow Col(int objectId, int columnId, string name, string type = "int") => new(
        objectId, columnId, name, "sys", type, 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null);

    private static CatalogSet OneTable(int objectId, string name, params (int id, string col)[] cols) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(objectId, "dbo", name, "U", DateTime.UnixEpoch, 0)],
        Columns = [.. cols.Select(c => Col(objectId, c.id, c.col))],
    };

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), SnapshotOptions.Default);

    [Fact]
    public void Table_with_identical_columns_is_flagged_as_rename()
    {
        // Kaynak: yeni ad. Hedef: eski ad. Kolonlar birebir aynı → rename adayı.
        var source = Build(OneTable(1, "CustomerV2", (1, "Id"), (2, "Name")));
        var target = Build(OneTable(1, "Customer", (1, "Id"), (2, "Name")));

        var result = SchemaComparer.Compare(source, target);
        var rename = Assert.Single(RenameDetector.Detect(result));
        Assert.Equal("Customer", rename.Removed.Name);
        Assert.Equal("CustomerV2", rename.Added.Name);
    }

    [Fact]
    public void Different_columns_are_not_a_rename()
    {
        var source = Build(OneTable(1, "CustomerV2", (1, "Id"), (2, "Email")));
        var target = Build(OneTable(1, "Customer", (1, "Id"), (2, "Name")));

        var result = SchemaComparer.Compare(source, target);
        Assert.Empty(RenameDetector.Detect(result));
    }

    [Fact]
    public void Ambiguous_matches_are_skipped()
    {
        // İki eklenen ve iki silinen tablo aynı yapıda → belirsiz, aday sayılmaz.
        var source = new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Objects = [new ObjectRow(1, "dbo", "A2", "U", DateTime.UnixEpoch, 0), new ObjectRow(2, "dbo", "B2", "U", DateTime.UnixEpoch, 0)],
            Columns = [Col(1, 1, "Id"), Col(2, 1, "Id")],
        };
        var target = new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Objects = [new ObjectRow(1, "dbo", "A", "U", DateTime.UnixEpoch, 0), new ObjectRow(2, "dbo", "B", "U", DateTime.UnixEpoch, 0)],
            Columns = [Col(1, 1, "Id"), Col(2, 1, "Id")],
        };

        var result = SchemaComparer.Compare(Build(source), Build(target));
        Assert.Empty(RenameDetector.Detect(result));
    }

    [Fact]
    public void No_rename_when_nothing_added_or_removed()
    {
        var t = OneTable(1, "Customer", (1, "Id"));
        var result = SchemaComparer.Compare(Build(t), Build(t));
        Assert.Empty(RenameDetector.Detect(result));
    }
}
