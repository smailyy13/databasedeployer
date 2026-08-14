using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// 6. bölüm: veritabanı ayarları ve plan guide'lar.
///
/// Dalga 16'da bu üç sınıf KARŞILAŞTIRMAYA girmiş ama hiçbir üreteç onları script'e
/// yazmıyordu — yani araç farkı gösterip script'e koymuyordu. Bu testler o boşluğu kapatır
/// ve bölümün modüllerden SONRA çalışmasını kilitler (OBJECT kapsamlı plan guide bağlı
/// olduğu prosedür yokken kurulamaz).
/// </summary>
public class SettingsScriptTests
{
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };
    private static readonly ObjectKey PlanGuideKey = new(string.Empty, "PG_Slow", ObjectKind.PlanGuide);

    private static CatalogSet Catalog(
        List<DatabaseScopedConfigurationRow>? settings = null,
        List<PlanGuideRow>? guides = null,
        List<LegacyRuleDefaultRow>? legacy = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        DatabaseScopedConfigurations = settings ?? [],
        PlanGuides = guides ?? [],
        LegacyRuleDefaults = legacy ?? [],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static SettingsScriptResult Script(CatalogSet source, CatalogSet target) =>
        SettingsScriptGenerator.Generate(SchemaComparer.Compare(Build(source), Build(target)));

    private static PlanGuideRow Guide(string? hints = "OPTION (MAXDOP 1)") =>
        new(1, "PG_Slow", false, "SQL", null, null, null, null, hints, "SELECT 1");

    // --- scoped configuration ---

    [Fact]
    public void Numeric_setting_is_written_as_a_number()
    {
        var script = Script(Catalog([new DatabaseScopedConfigurationRow(1, "MAXDOP", "4", "0")]), Catalog());

        Assert.Contains("ALTER DATABASE SCOPED CONFIGURATION SET MAXDOP = 4;", script.Sql);
    }

    [Fact]
    public void Boolean_setting_is_written_as_on_off_not_one_zero()
    {
        // Katalog 0/1 tutar ama T-SQL ON/OFF bekler; 1 yazmak geçersiz SQL olurdu.
        var on = Script(Catalog([new DatabaseScopedConfigurationRow(2, "LEGACY_CARDINALITY_ESTIMATION", "1", "0")]), Catalog());
        Assert.Contains("SET LEGACY_CARDINALITY_ESTIMATION = ON;", on.Sql);

        var off = Script(Catalog([new DatabaseScopedConfigurationRow(2, "PARAMETER_SNIFFING", "0", "0")]), Catalog());
        Assert.Contains("SET PARAMETER_SNIFFING = OFF;", off.Sql);
    }

    [Fact]
    public void Unchanged_setting_produces_nothing()
    {
        var same = Catalog([new DatabaseScopedConfigurationRow(1, "MAXDOP", "4", "0")]);

        Assert.True(Script(same, same).IsEmpty);
    }

    [Fact]
    public void Uninterpretable_value_is_skipped_with_a_reason_not_guessed()
    {
        var odd = Catalog([new DatabaseScopedConfigurationRow(9, "SOME_SETTING", "AUTO", "0")]);
        var script = Script(odd, Catalog());

        Assert.DoesNotContain("SOME_SETTING", script.Sql);
        Assert.Contains(script.Skipped, s => s.Reason.Contains("SOME_SETTING", StringComparison.Ordinal));
    }

    [Fact]
    public void Setting_removed_from_source_is_left_alone()
    {
        // Katalog yalnız varsayılandan sapanları verir: "kaynakta yok" = varsayılan, ve
        // varsayılan sürüme göre değişir. Tahmin etmektense dokunmuyoruz.
        var script = Script(Catalog(), Catalog([new DatabaseScopedConfigurationRow(1, "MAXDOP", "4", "0")]));

        Assert.True(script.IsEmpty);
    }

    // --- plan guide ---

    [Fact]
    public void New_plan_guide_is_created()
    {
        var script = Script(Catalog(guides: [Guide()]), Catalog());

        Assert.Contains("EXEC sp_create_plan_guide", script.Sql);
        Assert.Contains(PlanGuideKey, script.Included);
    }

    [Fact]
    public void Removed_plan_guide_is_dropped()
    {
        var script = Script(Catalog(), Catalog(guides: [Guide()]));

        Assert.Contains("EXEC sp_control_plan_guide N'DROP', N'PG_Slow';", script.Sql);
    }

    [Fact]
    public void Changed_plan_guide_is_dropped_then_recreated()
    {
        // Plan guide ALTER edilemez.
        var script = Script(Catalog(guides: [Guide("OPTION (MAXDOP 8)")]), Catalog(guides: [Guide()]));

        var drop = script.Sql.IndexOf("sp_control_plan_guide N'DROP'", StringComparison.Ordinal);
        var create = script.Sql.IndexOf("sp_create_plan_guide", StringComparison.Ordinal);

        Assert.True(drop >= 0 && create > drop, "önce düşürülmeli, sonra kurulmalı");
        Assert.Contains("MAXDOP 8", script.Sql);
    }

    [Fact]
    public void Nothing_to_do_produces_an_empty_result()
    {
        Assert.True(Script(Catalog(), Catalog()).IsEmpty);
    }

    // --- legacy RULE / DEFAULT (1. bölümde) ---

    [Fact]
    public void New_legacy_rule_is_created_by_the_type_generator()
    {
        var rule = new LegacyRuleDefaultRow(500, "dbo", "PositiveRule", "R", "CREATE RULE PositiveRule AS @x > 0");
        var script = TypeScriptGenerator.Generate(
            SchemaComparer.Compare(Build(Catalog(legacy: [rule])), Build(Catalog())), null, TypeScriptOptions.Default);

        Assert.Contains("CREATE RULE PositiveRule", script.Sql);
    }

    [Fact]
    public void Removed_rule_and_default_use_their_own_drop_verbs()
    {
        var rule = new LegacyRuleDefaultRow(500, "dbo", "R1", "R", "CREATE RULE R1 AS @x > 0");
        var def = new LegacyRuleDefaultRow(501, "dbo", "D1", "D", "CREATE DEFAULT D1 AS 0");
        var drops = new TypeScriptOptions { IncludeDrops = true };

        var script = TypeScriptGenerator.Generate(
            SchemaComparer.Compare(Build(Catalog()), Build(Catalog(legacy: [rule, def]))), null, drops);

        Assert.Contains("DROP RULE [dbo].[R1];", script.Sql);
        Assert.Contains("DROP DEFAULT [dbo].[D1];", script.Sql);
    }

    [Fact]
    public void Legacy_objects_are_handled_by_the_type_generator()
    {
        // Modül üretecinin "kapsam dışı" listesine düşmemeli.
        Assert.Contains(ObjectKind.LegacyRuleDefault, TypeScriptGenerator.HandledKinds);
    }
}
