using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

/// <summary>
/// Extended property'ler gerçek build hattından geçirilerek test edilir: ham katalog
/// satırları → SnapshotBuilder → karşılaştırma → ChangeCatalog. SnapshotBuilder internal,
/// InternalsVisibleTo ile erişilebilir.
/// </summary>
public class ExtendedPropertyTests
{
    private const int CustomerId = 100;

    private static CatalogSet Catalog(IEnumerable<ExtendedPropertyRow> eps, params ColumnRow[] columns)
    {
        return new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Schemas = [new SchemaRow(5, "sales")],
            Objects =
            [
                new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
            ],
            Columns = [.. columns],
            ExtendedProperties = [.. eps],
        };
    }

    private static ColumnRow Col(int columnId, string name) => new(
        CustomerId, columnId, name, "sys", "int", 4, 10, 0,
        true, null, false, false, null, null, null, null, null, null, null);

    private static DatabaseSnapshot Build(CatalogSet catalog) =>
        SnapshotBuilder.Build(catalog, new ExtractionReport(), SnapshotOptions.Default);

    [Fact]
    public void Object_level_property_added_makes_host_changed()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "Müşteri tablosu")]));
        var target = Build(Catalog([]));

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("extendedProperties", diff.ChangedParts);
    }

    [Fact]
    public void Identical_properties_produce_no_difference()
    {
        var ep = new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "aynı açıklama");
        var source = Build(Catalog([ep]));
        var target = Build(Catalog([ep]));

        var result = SchemaComparer.Compare(source, target);

        Assert.Empty(result.Differences);
        // Tablo + "sales" şeması + sentetik "(database)" objesi.
        Assert.Equal(3, result.EqualCount);
    }

    [Fact]
    public void Changed_property_value_is_detected()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "yeni")]));
        var target = Build(Catalog([new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "eski")]));

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences);
        Assert.Contains("extendedProperties", diff.ChangedParts);

        var change = Assert.Single(ChangeCatalog.Build(result));
        var child = Assert.Single(change.Children, c => c.Category == "Extended Properties");
        Assert.Equal(ChangeAction.Change, child.Action);
        Assert.Contains("eski", child.Detail);
        Assert.Contains("yeni", child.Detail);
    }

    [Fact]
    public void Column_level_property_is_scoped_to_the_column()
    {
        var source = Build(Catalog(
            [new ExtendedPropertyRow(1, CustomerId, 2, "MS_Description", "e-posta adresi")],
            Col(1, "Id"), Col(2, "Email")));
        var target = Build(Catalog([], Col(1, "Id"), Col(2, "Email")));

        var result = SchemaComparer.Compare(source, target);
        var change = Assert.Single(ChangeCatalog.Build(result));

        var child = Assert.Single(change.Children, c => c.Category == "Extended Properties");
        Assert.Equal(ChangeAction.Add, child.Action);
        Assert.Contains("col:Email", child.QualifiedName);
    }

    [Fact]
    public void Schema_level_property_makes_schema_changed()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(3, 5, 0, "MS_Description", "satış şeması")]));
        var target = Build(Catalog([]));

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences, d => d.Key.Kind == ObjectKind.Schema);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("extendedProperties", diff.ChangedParts);
    }

    [Fact]
    public void IgnoreExtendedProperties_suppresses_the_difference()
    {
        var options = SnapshotOptions.Default with { IgnoreExtendedProperties = true };
        var source = SnapshotBuilder.Build(
            Catalog([new ExtendedPropertyRow(1, CustomerId, 0, "MS_Description", "x")]), new ExtractionReport(), options);
        var target = SnapshotBuilder.Build(Catalog([]), new ExtractionReport(), options);

        var result = SchemaComparer.Compare(source, target);

        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Property_on_unknown_host_is_ignored()
    {
        // major_id bilinen hiçbir objeye ait değil → sessizce düşer, çökme yok.
        var source = Build(Catalog([new ExtendedPropertyRow(1, 999999, 0, "MS_Description", "hayalet")]));
        var target = Build(Catalog([]));

        var result = SchemaComparer.Compare(source, target);

        Assert.Empty(result.Differences);
    }
}
