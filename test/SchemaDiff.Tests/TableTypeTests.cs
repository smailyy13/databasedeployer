using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

public class TableTypeTests
{
    private const int InternalObjectId = 500;

    private static CatalogSet Catalog(string typeName, params TableTypeColumnRow[] columns) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        TableTypes = [new TableTypeRow(1, "dbo", typeName, InternalObjectId)],
        TableTypeColumns = [.. columns],
    };

    private static CatalogSet Empty() => new() { DatabaseName = "test", ServerName = "TESTSRV" };

    private static DatabaseSnapshot Build(CatalogSet catalog) =>
        SnapshotBuilder.Build(catalog, new ExtractionReport(), SnapshotOptions.Default);

    private static TableTypeColumnRow Col(
        int columnId, string name, string typeName = "int", short maxLength = 4, bool nullable = true) =>
        new(InternalObjectId, columnId, name, "sys", typeName, maxLength, 10, 0, nullable, null, false, false);

    [Fact]
    public void Table_type_only_in_source_is_Added()
    {
        var source = Build(Catalog("IdList", Col(1, "Id")));
        var target = Build(Empty());

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Added, diff.Kind);
        Assert.Equal(ObjectKind.TableType, diff.Key.Kind);
        Assert.Equal("IdList", diff.Key.Name);
    }

    [Fact]
    public void Identical_table_types_produce_no_difference()
    {
        var source = Build(Catalog("IdList", Col(1, "Id"), Col(2, "Name", "nvarchar", 100)));
        var target = Build(Catalog("IdList", Col(1, "Id"), Col(2, "Name", "nvarchar", 100)));

        var result = SchemaComparer.Compare(source, target);
        Assert.Empty(result.Differences);
        // Table type + sentetik "(database)" objesi.
        Assert.Equal(2, result.EqualCount);
    }

    [Fact]
    public void Different_column_set_is_Changed()
    {
        var source = Build(Catalog("IdList", Col(1, "Id"), Col(2, "Extra")));
        var target = Build(Catalog("IdList", Col(1, "Id")));

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("columns", diff.ChangedParts);
    }

    [Fact]
    public void Changed_column_type_is_detected()
    {
        var source = Build(Catalog("IdList", Col(1, "Id", "bigint", 8)));
        var target = Build(Catalog("IdList", Col(1, "Id", "int", 4)));

        var result = SchemaComparer.Compare(source, target);
        Assert.Single(result.Differences, d => d.Kind == DiffKind.Changed);
    }

    [Fact]
    public void Change_catalog_labels_it_table_type()
    {
        var source = Build(Catalog("IdList", Col(1, "Id")));
        var target = Build(Empty());

        var result = SchemaComparer.Compare(source, target);
        var change = Assert.Single(ChangeCatalog.Build(result));
        Assert.Equal("Table Type", change.ObjectType);
    }
}
