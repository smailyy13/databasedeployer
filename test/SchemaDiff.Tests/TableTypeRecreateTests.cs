using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Değişen table type'ın yeniden kurulması: bağımlı modülleri düşür → tipi drop+create →
/// modülleri geri kur.
///
/// Bu üretimin en tehlikeli tarafı bir prosedürü düşürüp GERİ KURAMAMAKTIR. Bu yüzden
/// varsayılan KAPALI ve açıkken bile ön koşul katı: bağımlıların TAMAMININ tanımı
/// okunabilir olmalı, aksi hâlde hiçbir şey üretilmez.
/// </summary>
public class TableTypeRecreateTests
{
    private const int TypeTableId = 500;
    private const int UserTypeId = 300;
    private const int ProcId = 700;

    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };
    private static readonly ObjectKey TypeKey = new("dbo", "IdList", ObjectKind.TableType);
    private static readonly TypeScriptOptions Recreate = new() { RecreateChangedTableTypes = true };

    private static TableTypeColumnRow Col(int id, string name) =>
        new(TypeTableId, id, name, "sys", "int", 4, 10, 0, true, null, false, false);

    private static CatalogSet Catalog(
        TableTypeColumnRow[]? columns = null, bool withDependent = true, string? procBody = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = withDependent
            ? [new ObjectRow(ProcId, "dbo", "UseList", "P", DateTime.UnixEpoch, 0)]
            : [],
        Modules = withDependent
            ? [new ModuleRow(ProcId, procBody ?? "CREATE PROCEDURE dbo.UseList @List dbo.IdList READONLY AS SELECT 1", true, true)]
            : [],
        TableTypes = [new TableTypeRow(UserTypeId, "dbo", "IdList", TypeTableId)],
        TableTypeColumns = [.. columns ?? [Col(1, "Id")]],
        TableTypeDependents = withDependent ? [new TableTypeDependentRow(UserTypeId, ProcId)] : [],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static TypeScriptResult Script(CatalogSet source, CatalogSet target, TypeScriptOptions? options = null) =>
        TypeScriptGenerator.Generate(
            SchemaComparer.Compare(Build(source), Build(target)), null, options ?? TypeScriptOptions.Default);

    private static CatalogSet Changed() => Catalog([Col(1, "Id"), Col(2, "Name")]);

    // --- bağımlılık tespiti ---

    [Fact]
    public void Dependent_modules_are_discovered_from_parameters()
    {
        // sys.sql_expression_dependencies parametre tipi bağımlılığını GÖRMEZ.
        var dependents = Build(Catalog()).Objects[TypeKey].DependentModules;

        Assert.Equal(new ObjectKey("dbo", "UseList", ObjectKind.Procedure), Assert.Single(dependents!));
    }

    // --- varsayılan: kapalı ---

    [Fact]
    public void Changed_type_is_skipped_by_default()
    {
        var script = Script(Changed(), Catalog());

        Assert.DoesNotContain("DROP TYPE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == TypeKey);
    }

    [Fact]
    public void Skip_message_points_at_the_option()
    {
        var script = Script(Changed(), Catalog());
        var skip = Assert.Single(script.Skipped, s => s.Key == TypeKey);

        Assert.Contains("yeniden kur", skip.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // --- açıkken: tam üretim ---

    [Fact]
    public void Recreate_drops_the_dependent_then_the_type_then_restores_both()
    {
        var sql = Script(Changed(), Catalog(), Recreate).Sql;

        var dropProc = sql.IndexOf("DROP PROCEDURE [dbo].[UseList];", StringComparison.Ordinal);
        var dropType = sql.IndexOf("DROP TYPE [dbo].[IdList];", StringComparison.Ordinal);
        var createType = sql.IndexOf("CREATE TYPE [dbo].[IdList] AS TABLE", StringComparison.Ordinal);
        var createProc = sql.IndexOf("CREATE PROCEDURE dbo.UseList", StringComparison.Ordinal);

        Assert.True(dropProc >= 0, "bağımlı prosedür düşürülmeli");
        Assert.True(dropProc < dropType, "prosedür tipten ÖNCE düşmeli");
        Assert.True(dropType < createType, "tip önce düşüp sonra kurulmalı");
        Assert.True(createType < createProc, "prosedür tip kurulduktan SONRA geri gelmeli");
    }

    [Fact]
    public void Recreated_type_carries_the_new_shape()
    {
        var sql = Script(Changed(), Catalog(), Recreate).Sql;

        Assert.Contains("[Name]", sql);
    }

    [Fact]
    public void Restored_module_keeps_its_set_options()
    {
        // SET seçenekleri korunmazsa modülün davranışı sessizce değişir.
        var sql = Script(Changed(), Catalog(), Recreate).Sql;

        Assert.Contains("SET ANSI_NULLS ON;", sql);
        Assert.Contains("SET QUOTED_IDENTIFIER ON;", sql);
    }

    [Fact]
    public void Drop_is_guarded_by_an_existence_check()
    {
        Assert.Contains("IF OBJECT_ID(N'[dbo].[UseList]') IS NOT NULL", Script(Changed(), Catalog(), Recreate).Sql);
    }

    [Fact]
    public void Recreated_type_counts_as_included()
    {
        Assert.Contains(TypeKey, Script(Changed(), Catalog(), Recreate).Included);
    }

    [Fact]
    public void Type_without_dependents_is_simply_dropped_and_recreated()
    {
        var sql = Script(Catalog([Col(1, "Id"), Col(2, "Name")], withDependent: false),
                         Catalog(withDependent: false), Recreate).Sql;

        Assert.Contains("DROP TYPE [dbo].[IdList];", sql);
        Assert.DoesNotContain("DROP PROCEDURE", sql);
    }

    // --- ön koşul: okunamayan bağımlı ---

    [Fact]
    public void Nothing_is_produced_when_a_dependent_cannot_be_restored()
    {
        // Şifrelenmiş/okunamayan prosedür: düşürüp geri kuramamak onarılamaz.
        var source = new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Objects = [new ObjectRow(ProcId, "dbo", "UseList", "P", DateTime.UnixEpoch, 0)],
            Modules = [new ModuleRow(ProcId, null, true, true)],   // definition NULL
            TableTypes = [new TableTypeRow(UserTypeId, "dbo", "IdList", TypeTableId)],
            TableTypeColumns = [Col(1, "Id"), Col(2, "Name")],
            TableTypeDependents = [new TableTypeDependentRow(UserTypeId, ProcId)],
        };

        var script = Script(source, Catalog(), Recreate);

        Assert.DoesNotContain("DROP TYPE", script.Sql);
        Assert.DoesNotContain("DROP PROCEDURE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == TypeKey && s.Reason.Contains("geri kurulamaz", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_dependent_is_named_in_the_reason()
    {
        var source = new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Objects = [new ObjectRow(ProcId, "dbo", "UseList", "P", DateTime.UnixEpoch, 0)],
            Modules = [new ModuleRow(ProcId, null, true, true)],
            TableTypes = [new TableTypeRow(UserTypeId, "dbo", "IdList", TypeTableId)],
            TableTypeColumns = [Col(1, "Id"), Col(2, "Name")],
            TableTypeDependents = [new TableTypeDependentRow(UserTypeId, ProcId)],
        };

        var skip = Assert.Single(Script(source, Catalog(), Recreate).Skipped, s => s.Key == TypeKey);

        Assert.Contains("UseList", skip.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Option_is_off_by_default()
    {
        // Prosedür düşüren bir üretim sessizce açık olmamalı.
        Assert.False(TypeScriptOptions.Default.RecreateChangedTableTypes);
    }
}
