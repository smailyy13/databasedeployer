using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

public class DdlTriggerTests
{
    private static CatalogSet Catalog(params DdlTriggerRow[] triggers) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        DdlTriggers = [.. triggers],
    };

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), SnapshotOptions.Default);

    private static DdlTriggerRow Trg(string name, string body, bool disabled = false) =>
        new(name, disabled, body, true, true);

    [Fact]
    public void Ddl_trigger_only_in_source_is_Added()
    {
        var source = Build(Catalog(Trg("trgAudit", "CREATE TRIGGER trgAudit ON DATABASE FOR CREATE_TABLE AS SELECT 1")));
        var target = Build(Catalog());

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Added, diff.Kind);
        Assert.Equal(ObjectKind.DdlTrigger, diff.Key.Kind);
        Assert.Equal("trgAudit", diff.Key.Name);

        var change = Assert.Single(ChangeCatalog.Build(result));
        Assert.Equal("DDL Trigger", change.ObjectType);
    }

    [Fact]
    public void Identical_ddl_triggers_produce_no_difference()
    {
        var t = Trg("trgAudit", "CREATE TRIGGER trgAudit ON DATABASE FOR DROP_TABLE AS SELECT 1");
        var result = SchemaComparer.Compare(Build(Catalog(t)), Build(Catalog(t)));
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Changed_body_is_detected()
    {
        var source = Build(Catalog(Trg("t", "CREATE TRIGGER t ON DATABASE FOR CREATE_TABLE AS SELECT 2")));
        var target = Build(Catalog(Trg("t", "CREATE TRIGGER t ON DATABASE FOR CREATE_TABLE AS SELECT 1")));

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("body", diff.ChangedParts);
    }

    [Fact]
    public void Disabled_state_difference_is_detected()
    {
        var source = Build(Catalog(Trg("t", "CREATE TRIGGER t ON DATABASE FOR CREATE_TABLE AS SELECT 1", disabled: true)));
        var target = Build(Catalog(Trg("t", "CREATE TRIGGER t ON DATABASE FOR CREATE_TABLE AS SELECT 1", disabled: false)));

        var result = SchemaComparer.Compare(source, target);
        Assert.Single(result.Differences, d => d.ChangedParts.Contains("attributes"));
    }

    [Fact]
    public void Comment_only_difference_is_ignored()
    {
        var source = Build(Catalog(Trg("t", "CREATE TRIGGER t ON DATABASE FOR CREATE_TABLE AS SELECT 1 -- v2")));
        var target = Build(Catalog(Trg("t", "CREATE TRIGGER t ON DATABASE FOR CREATE_TABLE AS SELECT 1")));

        var result = SchemaComparer.Compare(source, target);
        Assert.Empty(result.Differences);
    }
}
