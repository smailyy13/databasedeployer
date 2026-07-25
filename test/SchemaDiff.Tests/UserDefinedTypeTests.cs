using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

public class UserDefinedTypeTests
{
    private static CatalogSet Catalog(params UserDefinedTypeRow[] types) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        UserDefinedTypes = [.. types],
    };

    private static DatabaseSnapshot Build(CatalogSet catalog) =>
        SnapshotBuilder.Build(catalog, new ExtractionReport(), SnapshotOptions.Default);

    private static UserDefinedTypeRow Udt(
        string name, string baseType, short maxLength = 4, byte precision = 19, byte scale = 4,
        bool nullable = true, string? collation = null) =>
        new(1, "dbo", name, baseType, maxLength, precision, scale, nullable, collation);

    [Fact]
    public void Type_only_in_source_is_Added()
    {
        var source = Build(Catalog(Udt("Money", "decimal")));
        var target = Build(Catalog());

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Added, diff.Kind);
        Assert.Equal(ObjectKind.UserDefinedType, diff.Key.Kind);
        Assert.Equal("Money", diff.Key.Name);
    }

    [Fact]
    public void Type_only_in_target_is_Removed()
    {
        var source = Build(Catalog());
        var target = Build(Catalog(Udt("Legacy", "int")));

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Removed, diff.Kind);
    }

    [Fact]
    public void Identical_types_produce_no_difference()
    {
        var source = Build(Catalog(Udt("Money", "decimal", precision: 19, scale: 4)));
        var target = Build(Catalog(Udt("Money", "decimal", precision: 19, scale: 4)));

        var result = SchemaComparer.Compare(source, target);
        Assert.Empty(result.Differences);
        // UDT + sentetik "(database)" objesi.
        Assert.Equal(2, result.EqualCount);
    }

    [Fact]
    public void Changed_precision_is_detected()
    {
        var source = Build(Catalog(Udt("Money", "decimal", precision: 19, scale: 4)));
        var target = Build(Catalog(Udt("Money", "decimal", precision: 10, scale: 2)));

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
    }

    [Fact]
    public void Changed_nullability_is_detected()
    {
        var source = Build(Catalog(Udt("Code", "varchar", maxLength: 10, nullable: false)));
        var target = Build(Catalog(Udt("Code", "varchar", maxLength: 10, nullable: true)));

        var result = SchemaComparer.Compare(source, target);
        Assert.Single(result.Differences, d => d.Kind == DiffKind.Changed);
    }

    [Fact]
    public void Change_catalog_labels_it_user_defined_type()
    {
        var source = Build(Catalog(Udt("Money", "decimal")));
        var target = Build(Catalog());

        var result = SchemaComparer.Compare(source, target);
        var change = Assert.Single(ChangeCatalog.Build(result));
        Assert.Equal("User-Defined Type", change.ObjectType);
    }
}
