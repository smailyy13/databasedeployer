using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

/// <summary>
/// Veritabanı seviyesi (class 0) extended property'ler ve izinler sentetik "(database)"
/// objesine bağlanır. Anahtar sabit — db adı ortamlar arası değişse de fark üretmez.
/// </summary>
public class DatabaseLevelTests
{
    private static CatalogSet Db(string name,
        IEnumerable<ExtendedPropertyRow>? eps = null, IEnumerable<PermissionRow>? perms = null) => new()
    {
        DatabaseName = name,
        ServerName = "TESTSRV",
        ExtendedProperties = [.. eps ?? []],
        Permissions = [.. perms ?? []],
    };

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), SnapshotOptions.Default);

    [Fact]
    public void Different_database_names_alone_produce_no_difference()
    {
        // Sabit "(database)" anahtarı sayesinde db adı farkı fark yaratmamalı.
        var result = SchemaComparer.Compare(Build(Db("DevDb")), Build(Db("ProdDb")));
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Database_level_extended_property_difference_is_detected()
    {
        var source = Build(Db("Dev", eps: [new ExtendedPropertyRow(0, 0, 0, "MS_Description", "Ana veri ambarı")]));
        var target = Build(Db("Prod"));

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences, d => d.Key.Kind == ObjectKind.Database);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("extendedProperties", diff.ChangedParts);

        var change = Assert.Single(ChangeCatalog.Build(result), c => c.ObjectType == "Database");
        Assert.Contains(change.Children, c => c.Category == "Extended Properties");
    }

    [Fact]
    public void Database_level_permission_difference_is_detected()
    {
        var source = Build(Db("Dev", perms: [new PermissionRow(0, 0, 0, "VIEW DEFINITION", "GRANT", "app_reader")]));
        var target = Build(Db("Prod"));

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences, d => d.Key.Kind == ObjectKind.Database);
        Assert.Contains("permissions", diff.ChangedParts);
    }

    [Fact]
    public void Identical_database_level_metadata_produces_no_difference()
    {
        var ep = new ExtendedPropertyRow(0, 0, 0, "MS_Description", "aynı");
        var result = SchemaComparer.Compare(Build(Db("Dev", eps: [ep])), Build(Db("Prod", eps: [ep])));
        Assert.Empty(result.Differences);
    }
}
