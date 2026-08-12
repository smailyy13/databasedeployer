using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

/// <summary>
/// Plan guide · database scoped configuration · legacy CREATE RULE / CREATE DEFAULT.
///
/// Üçü de "nadir ama varsa önemli" sınıfından: özellikle scoped configuration (MAXDOP,
/// legacy cardinality estimation) dev ile prod arasında sessizce farklı olduğunda sorgu
/// planları değişir ve bunu şema karşılaştırması yakalamazsa hiçbir şey yakalamaz.
/// </summary>
public class PlanGuideAndSettingsTests
{
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };
    private static readonly ObjectKey DatabaseKey = new(string.Empty, "(database)", ObjectKind.Database);

    private static PlanGuideRow Guide(
        string name = "PG_Slow", bool disabled = false, string? hints = "OPTION (MAXDOP 1)",
        string scopeType = "OBJECT", string? scopeSchema = "dbo", string? scopeObject = "GetCustomer") =>
        new(1, name, disabled, scopeType, scopeSchema, scopeObject, null, "@Id int", hints, "SELECT * FROM T");

    private static CatalogSet Catalog(
        List<PlanGuideRow>? guides = null,
        List<DatabaseScopedConfigurationRow>? settings = null,
        List<LegacyRuleDefaultRow>? legacy = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        PlanGuides = guides ?? [],
        DatabaseScopedConfigurations = settings ?? [],
        LegacyRuleDefaults = legacy ?? [],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    // --- plan guide ---

    [Fact]
    public void Plan_guide_is_a_database_level_object()
    {
        var key = new ObjectKey(string.Empty, "PG_Slow", ObjectKind.PlanGuide);

        Assert.True(Build(Catalog([Guide()])).Objects.ContainsKey(key));
        Assert.Equal("PLAN GUIDE [PG_Slow]", key.ToString());
    }

    [Fact]
    public void Plan_guide_hint_difference_is_detected()
    {
        Assert.Single(SchemaComparer.Compare(
            Build(Catalog([Guide(hints: "OPTION (MAXDOP 4)")])),
            Build(Catalog([Guide()]))).Differences);
    }

    [Fact]
    public void Plan_guide_is_scripted_with_sp_create_plan_guide()
    {
        var script = Build(Catalog([Guide()])).Objects[new ObjectKey(string.Empty, "PG_Slow", ObjectKind.PlanGuide)].DisplayScript!;

        Assert.Contains("EXEC sp_create_plan_guide", script);
        Assert.Contains("@name = N'PG_Slow'", script);
        Assert.Contains("@type = N'OBJECT'", script);
        Assert.Contains("@module_or_batch = N'[dbo].[GetCustomer]'", script);
        Assert.Contains("@hints = N'OPTION (MAXDOP 1)'", script);
    }

    [Fact]
    public void Disabled_plan_guide_is_created_then_disabled()
    {
        // sp_create_plan_guide her zaman ETKİN kurar; pasiflik ayrı çağrıdır.
        var script = Build(Catalog([Guide(disabled: true)])).Objects[new ObjectKey(string.Empty, "PG_Slow", ObjectKind.PlanGuide)].DisplayScript!;

        var create = script.IndexOf("sp_create_plan_guide", StringComparison.Ordinal);
        var disable = script.IndexOf("sp_control_plan_guide N'DISABLE'", StringComparison.Ordinal);

        Assert.True(create >= 0 && disable > create, "önce kurulmalı, sonra kapatılmalı");
    }

    [Fact]
    public void Quotes_in_the_query_text_are_escaped()
    {
        // Sorgu metni serbest metindir; kaçırılmazsa sp_create_plan_guide çağrısı bozulur.
        var quoted = new PlanGuideRow(1, "PG_Q", false, "SQL", null, null, null, null, null, "SELECT 'x'");
        var script = Build(Catalog([quoted])).Objects[new ObjectKey(string.Empty, "PG_Q", ObjectKind.PlanGuide)].DisplayScript!;

        Assert.Contains("@stmt = N'SELECT ''x'''", script);
    }

    [Fact]
    public void Plan_guide_scope_is_compared_by_name_not_object_id()
    {
        // scope_object_id ortama özgüdür; sorgu zaten ADI çekiyor.
        var canonical = Build(Catalog([Guide()]))
            .Objects[new ObjectKey(string.Empty, "PG_Slow", ObjectKind.PlanGuide)].PartCanonical["definition"];

        Assert.Contains("scope=[dbo].[GetCustomer]", canonical, StringComparison.Ordinal);
    }

    // --- database scoped configuration ---

    [Fact]
    public void Scoped_configuration_is_part_of_the_database_object()
    {
        // Ayrı obje değil: hepsi tek bir ALTER DATABASE SCOPED CONFIGURATION ailesidir.
        var db = Build(Catalog(settings: [new DatabaseScopedConfigurationRow(1, "MAXDOP", "4", "0")]));

        Assert.Contains("dbconfig|MAXDOP|value=4", db.Objects[DatabaseKey].PartCanonical["scopedConfiguration"], StringComparison.Ordinal);
    }

    [Fact]
    public void Scoped_configuration_difference_is_detected()
    {
        var four = Build(Catalog(settings: [new DatabaseScopedConfigurationRow(1, "MAXDOP", "4", "0")]));
        var eight = Build(Catalog(settings: [new DatabaseScopedConfigurationRow(1, "MAXDOP", "8", "0")]));

        var diff = Assert.Single(SchemaComparer.Compare(four, eight).Differences);
        Assert.Contains("scopedConfiguration", diff.ChangedParts);
    }

    [Fact]
    public void Setting_order_does_not_affect_the_hash()
    {
        var a = Build(Catalog(settings:
        [
            new DatabaseScopedConfigurationRow(1, "MAXDOP", "4", "0"),
            new DatabaseScopedConfigurationRow(2, "LEGACY_CARDINALITY_ESTIMATION", "1", "0"),
        ]));
        var b = Build(Catalog(settings:
        [
            new DatabaseScopedConfigurationRow(2, "LEGACY_CARDINALITY_ESTIMATION", "1", "0"),
            new DatabaseScopedConfigurationRow(1, "MAXDOP", "4", "0"),
        ]));

        Assert.Empty(SchemaComparer.Compare(a, b).Differences);
    }

    [Fact]
    public void Change_catalog_lists_settings_under_their_own_folder()
    {
        var four = Build(Catalog(settings: [new DatabaseScopedConfigurationRow(1, "MAXDOP", "4", "0")]));
        var eight = Build(Catalog(settings: [new DatabaseScopedConfigurationRow(1, "MAXDOP", "8", "0")]));
        var change = Assert.Single(ChangeCatalog.Build(SchemaComparer.Compare(four, eight)));

        Assert.Contains(change.Children, c => c.Category == "Database Settings");
    }

    // --- legacy RULE / DEFAULT ---

    [Fact]
    public void Legacy_rule_is_an_object_with_its_body()
    {
        var rule = new LegacyRuleDefaultRow(500, "dbo", "PositiveRule", "R", "CREATE RULE PositiveRule AS @x > 0");
        var key = new ObjectKey("dbo", "PositiveRule", ObjectKind.LegacyRuleDefault);
        var snapshot = Build(Catalog(legacy: [rule])).Objects[key];

        Assert.True(snapshot.Parts.ContainsKey("body"));
        Assert.Contains("CREATE RULE", snapshot.DisplayScript!);
    }

    [Fact]
    public void Legacy_body_difference_is_detected()
    {
        var a = new LegacyRuleDefaultRow(500, "dbo", "R1", "R", "CREATE RULE R1 AS @x > 0");
        var b = new LegacyRuleDefaultRow(500, "dbo", "R1", "R", "CREATE RULE R1 AS @x > 10");

        Assert.Single(SchemaComparer.Compare(Build(Catalog(legacy: [a])), Build(Catalog(legacy: [b]))).Differences);
    }

    [Fact]
    public void Unreadable_legacy_body_is_indeterminate_not_equal()
    {
        // Sessiz eşitlik en tehlikeli sonuç.
        var encrypted = new LegacyRuleDefaultRow(500, "dbo", "R1", "R", null);
        var result = SchemaComparer.Compare(Build(Catalog(legacy: [encrypted])), Build(Catalog(legacy: [encrypted])));

        Assert.Equal(DiffKind.Indeterminate, Assert.Single(result.Differences).Kind);
    }

    [Fact]
    public void Rule_and_default_share_one_object_kind_but_keep_their_type()
    {
        var rule = new LegacyRuleDefaultRow(500, "dbo", "X", "R", "CREATE RULE X AS @v > 0");
        var def = new LegacyRuleDefaultRow(500, "dbo", "X", "D", "CREATE DEFAULT X AS 0");

        Assert.Single(SchemaComparer.Compare(Build(Catalog(legacy: [rule])), Build(Catalog(legacy: [def]))).Differences);
    }

    // --- kapsam ---

    [Theory]
    [InlineData("Plan guide'lar")]
    [InlineData("Database scoped configuration'lar")]
    [InlineData("CREATE RULE / CREATE DEFAULT (bağlı objeler)")]
    public void Coverage_probe_marks_them_as_covered(string item)
    {
        Assert.True(Assert.Single(CoverageProbe.Probes, p => p.Item == item).Covered, item);
    }

    [Theory]
    [InlineData(ObjectKind.PlanGuide, "Plan Guide")]
    [InlineData(ObjectKind.LegacyRuleDefault, "Rule or Default")]
    public void Labels_round_trip(ObjectKind kind, string label)
    {
        Assert.Equal(label, ObjectKindLabels.Display(kind));
        Assert.Equal(kind, ObjectKindLabels.Resolve(label));
    }
}
