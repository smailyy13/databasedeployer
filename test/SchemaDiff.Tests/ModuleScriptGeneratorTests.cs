using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Tests.TestFactory;

namespace SchemaDiff.Tests;

public class ModuleScriptGeneratorTests
{
    private static ScriptResult Generate(
        DatabaseSnapshot source, DatabaseSnapshot target, ISet<ObjectKey>? selection = null)
    {
        var result = SchemaComparer.Compare(source, target);
        return ModuleScriptGenerator.Generate(result, selection, ScriptOptions.Default);
    }

    [Fact]
    public void Added_view_is_emitted_as_create()
    {
        var key = View("NewView");
        var source = Database("dev", [Obj(key, 1, displayScript: "CREATE VIEW [dbo].[NewView] AS SELECT 1 AS x")]);
        var target = Database("prod", []);

        var script = Generate(source, target);

        Assert.Contains(key, script.Included);
        Assert.Contains("CREATE VIEW [dbo].[NewView]", script.Sql);
    }

    [Fact]
    public void Changed_proc_is_emitted_as_alter()
    {
        var key = Proc("P");
        var source = Database("dev", [Obj(key, 1, displayScript: "CREATE PROCEDURE [dbo].[P] AS SELECT 2")]);
        var target = Database("prod", [Obj(key, 2, displayScript: "CREATE PROCEDURE [dbo].[P] AS SELECT 1")]);

        var script = Generate(source, target);

        Assert.Contains(key, script.Included);
        Assert.Contains("ALTER PROCEDURE [dbo].[P]", script.Sql);
        Assert.DoesNotContain("CREATE PROCEDURE [dbo].[P]", script.Sql);
    }

    [Fact]
    public void Removed_module_is_dropped_with_object_id_guard()
    {
        var key = View("Legacy");
        var source = Database("dev", []);
        var target = Database("prod", [Obj(key, 1, displayScript: "CREATE VIEW [dbo].[Legacy] AS SELECT 1")]);

        var script = Generate(source, target);

        Assert.Contains(key, script.Included);
        Assert.Contains("DROP VIEW [dbo].[Legacy]", script.Sql);
        Assert.Contains("OBJECT_ID(N'[dbo].[Legacy]', N'V')", script.Sql);
    }

    [Fact]
    public void Removed_module_is_out_of_scope_when_drops_disabled()
    {
        var key = View("Legacy");
        var source = Database("dev", []);
        var target = Database("prod", [Obj(key, 1, displayScript: "CREATE VIEW [dbo].[Legacy] AS SELECT 1")]);
        var result = SchemaComparer.Compare(source, target);

        var script = ModuleScriptGenerator.Generate(result, selection: null, new ScriptOptions { IncludeDrops = false });

        Assert.Contains(key, script.OutOfScope);
        Assert.DoesNotContain("DROP VIEW", script.Sql);
    }

    [Fact]
    public void Table_change_is_out_of_scope_and_listed_in_header()
    {
        var tableKey = Table("Orders");
        var source = Database("dev", [Obj(tableKey, 1, columns: [Column("Id")])]);
        var target = Database("prod", [Obj(tableKey, 2, columns: [Column("Id"), Column("Old")])]);

        var script = Generate(source, target);

        Assert.Contains(tableKey, script.OutOfScope);
        Assert.Empty(script.Included);
        // Kapsam dışı obje sessizce düşmez; başlıkta ismen görünür.
        Assert.Contains("Orders", script.Sql);
        Assert.Contains("KAPSAM DIŞI", script.Sql);
    }

    [Fact]
    public void Table_is_not_listed_out_of_scope_when_handled_elsewhere()
    {
        var tableKey = Table("Orders");
        var source = Database("dev", [Obj(tableKey, 1, columns: [Column("Id")])]);
        var target = Database("prod", [Obj(tableKey, 2, columns: [Column("Id"), Column("Old")])]);
        var result = SchemaComparer.Compare(source, target);

        var script = ModuleScriptGenerator.Generate(result, selection: null,
            new ScriptOptions { TablesHandledElsewhere = true });

        Assert.DoesNotContain(tableKey, script.OutOfScope);
        Assert.DoesNotContain("KAPSAM DIŞI", script.Sql);
    }

    [Fact]
    public void Missing_schema_is_created()
    {
        var schemaKey = Schema("staging");
        var source = Database("dev", [Obj(schemaKey, 1)]);
        var target = Database("prod", []);

        var script = Generate(source, target);

        Assert.Contains(schemaKey, script.Included);
        Assert.Contains("SCHEMA_ID(N'staging')", script.Sql);
        Assert.Contains("CREATE SCHEMA [staging]", script.Sql);
    }

    [Fact]
    public void New_objects_are_emitted_in_dependency_order()
    {
        // ViewB, ViewA'ya referans veriyor → ViewA önce oluşturulmalı.
        var a = View("ViewA");
        var b = View("ViewB");
        var references = new Dictionary<ObjectKey, List<ObjectKey>>(ObjectKeyComparer.CaseInsensitive)
        {
            [b] = [a],
        };
        var source = Database("dev",
            [
                Obj(a, 1, displayScript: "CREATE VIEW [dbo].[ViewA] AS SELECT 1 AS x"),
                Obj(b, 2, displayScript: "CREATE VIEW [dbo].[ViewB] AS SELECT x FROM [dbo].[ViewA]"),
            ],
            references);
        var target = Database("prod", []);

        var script = Generate(source, target);

        var indexA = script.Sql.IndexOf("[dbo].[ViewA]", StringComparison.Ordinal);
        var indexB = script.Sql.IndexOf("CREATE VIEW [dbo].[ViewB]", StringComparison.Ordinal);
        Assert.True(indexA >= 0 && indexB >= 0);
        Assert.True(indexA < indexB, "ViewA, ViewB'den önce oluşturulmalı");
        Assert.False(script.HadDependencyCycle);
    }

    [Fact]
    public void Trigger_whose_parent_table_is_absent_in_target_is_skipped()
    {
        var parent = Table("NewTable");
        var trigger = Trigger("trg_NewTable");
        var source = Database("dev",
        [
            Obj(parent, 1, columns: [Column("Id")]),
            Obj(trigger, 2, displayScript: "CREATE TRIGGER [dbo].[trg_NewTable] ON [dbo].[NewTable] AFTER INSERT AS SELECT 1",
                parent: parent),
        ]);
        var target = Database("prod", []); // parent tablo hedefte yok

        var script = Generate(source, target);

        Assert.Contains(script.Skipped, s => s.Key == trigger);
        Assert.DoesNotContain(trigger, script.Included);
    }

    [Fact]
    public void Indeterminate_object_is_skipped_with_reason()
    {
        var key = Proc("Encrypted");
        var source = Database("dev", [Obj(key, 1, incomparable: true)]);
        var target = Database("prod", [Obj(key, 1, incomparable: true)]);

        var script = Generate(source, target);

        Assert.Contains(script.Skipped, s => s.Key == key);
    }

    [Fact]
    public void Selection_limits_output_to_chosen_keys()
    {
        var wanted = View("Wanted");
        var ignored = View("Ignored");
        var source = Database("dev",
        [
            Obj(wanted, 1, displayScript: "CREATE VIEW [dbo].[Wanted] AS SELECT 1 AS x"),
            Obj(ignored, 2, displayScript: "CREATE VIEW [dbo].[Ignored] AS SELECT 1 AS x"),
        ]);
        var target = Database("prod", []);
        var selection = new HashSet<ObjectKey>(ObjectKeyComparer.CaseInsensitive) { wanted };

        var script = Generate(source, target, selection);

        Assert.Contains(wanted, script.Included);
        Assert.DoesNotContain("Ignored", script.Sql);
    }

    [Fact]
    public void Emits_set_options_matching_module_metadata()
    {
        var key = View("V");
        var source = Database("dev",
            [Obj(key, 1, displayScript: "CREATE VIEW [dbo].[V] AS SELECT 1 AS x",
                ansiNulls: true, quotedIdentifier: false)]);
        var target = Database("prod", []);

        var script = Generate(source, target);

        Assert.Contains("SET ANSI_NULLS ON;", script.Sql);
        Assert.Contains("SET QUOTED_IDENTIFIER OFF;", script.Sql);
    }
}
