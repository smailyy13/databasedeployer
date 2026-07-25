using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

public class PartitionTests
{
    private static DatabaseSnapshot Build(CatalogSet catalog) =>
        SnapshotBuilder.Build(catalog, new ExtractionReport(), SnapshotOptions.Default);

    private static CatalogSet FunctionCatalog(
        string name, bool right, string type, params string[] bounds)
    {
        var values = bounds.Select((b, i) => new PartitionRangeValueRow(1, i, b)).ToList();
        return new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            PartitionFunctions = [new PartitionFunctionRow(1, name, right, type)],
            PartitionRangeValues = values,
        };
    }

    private static CatalogSet SchemeCatalog(string name, string function, params string[] filegroups)
    {
        var files = filegroups.Select((f, i) => new PartitionSchemeFileRow(70, i, f)).ToList();
        return new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            PartitionSchemes = [new PartitionSchemeRow(70, name, function)],
            PartitionSchemeFiles = files,
        };
    }

    private static CatalogSet Empty() => new() { DatabaseName = "test", ServerName = "TESTSRV" };

    [Fact]
    public void Partition_function_only_in_source_is_Added()
    {
        var source = Build(FunctionCatalog("pfMonthly", true, "datetime2", "2025-01-01", "2025-02-01"));
        var target = Build(Empty());

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Added, diff.Kind);
        Assert.Equal(ObjectKind.PartitionFunction, diff.Key.Kind);
        Assert.Equal("pfMonthly", diff.Key.Name);
    }

    [Fact]
    public void Identical_functions_produce_no_difference()
    {
        var source = Build(FunctionCatalog("pf", true, "int", "10", "20", "30"));
        var target = Build(FunctionCatalog("pf", true, "int", "10", "20", "30"));

        var result = SchemaComparer.Compare(source, target);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Different_boundary_values_are_Changed()
    {
        var source = Build(FunctionCatalog("pf", true, "int", "10", "20", "30"));
        var target = Build(FunctionCatalog("pf", true, "int", "10", "20"));

        var result = SchemaComparer.Compare(source, target);
        Assert.Single(result.Differences, d => d.Kind == DiffKind.Changed);
    }

    [Fact]
    public void Different_range_direction_is_Changed()
    {
        var source = Build(FunctionCatalog("pf", true, "int", "10"));
        var target = Build(FunctionCatalog("pf", false, "int", "10"));

        var result = SchemaComparer.Compare(source, target);
        Assert.Single(result.Differences, d => d.Kind == DiffKind.Changed);
    }

    [Fact]
    public void Partition_scheme_labeled_and_added()
    {
        var source = Build(SchemeCatalog("psMonthly", "pfMonthly", "PRIMARY", "FG1", "FG2"));
        var target = Build(Empty());

        var result = SchemaComparer.Compare(source, target);
        var change = Assert.Single(ChangeCatalog.Build(result));
        Assert.Equal("Partition Scheme", change.ObjectType);
        Assert.Equal(DiffKind.Added, result.Differences[0].Kind);
    }

    [Fact]
    public void Scheme_bound_to_different_function_is_Changed()
    {
        var source = Build(SchemeCatalog("ps", "pfNew", "PRIMARY"));
        var target = Build(SchemeCatalog("ps", "pfOld", "PRIMARY"));

        var result = SchemaComparer.Compare(source, target);
        Assert.Single(result.Differences, d => d.Kind == DiffKind.Changed);
    }
}
