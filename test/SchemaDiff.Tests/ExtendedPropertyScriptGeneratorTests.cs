using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

public class ExtendedPropertyScriptGeneratorTests
{
    private const int CustomerId = 100;

    private static CatalogSet Catalog(IEnumerable<ExtendedPropertyRow> eps, params ColumnRow[] columns) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Schemas = [new SchemaRow(5, "sales")],
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [.. columns],
        ExtendedProperties = [.. eps],
    };

    private static ColumnRow Col(int id, string name) => new(
        CustomerId, id, name, "sys", "int", 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null);

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), SnapshotOptions.Default);

    private static ExtendedPropertyScriptResult Generate(DatabaseSnapshot source, DatabaseSnapshot target) =>
        ExtendedPropertyScriptGenerator.Generate(SchemaComparer.Compare(source, target));

    [Fact]
    public void Added_object_property_emits_add()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "Müşteri")]));
        var target = Build(Catalog([]));

        var script = Generate(source, target);

        Assert.Contains("sp_addextendedproperty", script.Sql);
        Assert.Contains("@name = N'MS_Description'", script.Sql);
        Assert.Contains("@value = N'Müşteri'", script.Sql);
        Assert.Contains("@level0type = N'SCHEMA', @level0name = N'dbo'", script.Sql);
        Assert.Contains("@level1type = N'TABLE', @level1name = N'Customer'", script.Sql);
    }

    [Fact]
    public void Changed_value_emits_update()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "yeni")]));
        var target = Build(Catalog([new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "eski")]));

        var script = Generate(source, target);

        Assert.Contains("sp_updateextendedproperty", script.Sql);
        Assert.Contains("@value = N'yeni'", script.Sql);
    }

    [Fact]
    public void Removed_property_emits_drop_without_value()
    {
        var source = Build(Catalog([]));
        var target = Build(Catalog([new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "eski")]));

        var script = Generate(source, target);

        Assert.Contains("sp_dropextendedproperty", script.Sql);
        Assert.DoesNotContain("@value", script.Sql);
    }

    [Fact]
    public void Column_property_adds_level2()
    {
        var cols = new[] { Col(1, "Id"), Col(2, "Email") };
        var source = Build(Catalog([new ExtendedPropertyRow(1, CustomerId, 2, "MS_Description", "e-posta")], cols));
        var target = Build(Catalog([], cols));

        var script = Generate(source, target);

        Assert.Contains("@level2type = N'COLUMN', @level2name = N'Email'", script.Sql);
    }

    [Fact]
    public void Schema_property_uses_only_level0()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(3, 5, 0, "MS_Description", "satış")]));
        var target = Build(Catalog([]));

        var script = Generate(source, target);

        Assert.Contains("@level0type = N'SCHEMA', @level0name = N'sales'", script.Sql);
        Assert.DoesNotContain("@level1type", script.Sql);
    }

    [Fact]
    public void No_property_differences_produce_empty()
    {
        var ep = new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "aynı");
        var script = Generate(Build(Catalog([ep])), Build(Catalog([ep])));

        Assert.True(script.IsEmpty);
    }

    [Fact]
    public void Value_with_apostrophe_is_escaped()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "O'Brien")]));
        var target = Build(Catalog([]));

        var script = Generate(source, target);

        Assert.Contains("N'O''Brien'", script.Sql);
    }
}
