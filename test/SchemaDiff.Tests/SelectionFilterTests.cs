using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Tests.TestFactory;

namespace SchemaDiff.Tests;

/// <summary>
/// Seçim (checkbox) filtresi: kullanıcı bir objenin tikini kaldırınca o obje —
/// Add / Change / Delete fark etmeksizin — üretilen script'e GİRMEMELİ.
/// Rapor edilen bug: Add'te seçim çalışıyordu ama Change/Delete'te tik kaldırma yok sayılıyordu.
/// </summary>
public class SelectionFilterTests
{
    // Add: kaynakta var, hedefte yok. Change: ikisinde var, farklı. Delete: hedefte var, kaynakta yok.
    private static (DatabaseSnapshot src, DatabaseSnapshot tgt, ObjectKey add, ObjectKey chg, ObjectKey del) Mixed()
    {
        var add = View("AddedView");
        var chg = Proc("ChangedProc");
        var del = View("DeletedView");

        var source = Database("dev",
        [
            Obj(add, 1, displayScript: "CREATE VIEW [dbo].[AddedView] AS SELECT 1 AS x"),
            Obj(chg, 2, displayScript: "CREATE PROCEDURE [dbo].[ChangedProc] AS SELECT 2"),
        ]);
        var target = Database("prod",
        [
            Obj(chg, 3, displayScript: "CREATE PROCEDURE [dbo].[ChangedProc] AS SELECT 1"),
            Obj(del, 4, displayScript: "CREATE VIEW [dbo].[DeletedView] AS SELECT 1"),
        ]);
        return (source, target, add, chg, del);
    }

    private static ISet<ObjectKey> Only(params ObjectKey[] keys)
    {
        var set = new HashSet<ObjectKey>(ObjectKeyComparer.CaseInsensitive);
        foreach (var k in keys) set.Add(k);
        return set;
    }

    [Fact]
    public void Deselecting_change_and_delete_excludes_them_keeps_add()
    {
        var (src, tgt, add, chg, del) = Mixed();
        var result = SchemaComparer.Compare(src, tgt);

        // Kullanıcı yalnız Add'i seçili bırakıyor (Change ve Delete tiki kaldırılmış).
        var script = ModuleScriptGenerator.Generate(result, Only(add), ScriptOptions.Default);

        Assert.Contains(add, script.Included);
        Assert.Contains("AddedView", script.Sql);
        Assert.DoesNotContain(chg, script.Included);
        Assert.DoesNotContain(del, script.Included);
        Assert.DoesNotContain("ChangedProc", script.Sql);   // Change tiki kaldırıldı → yazılmamalı
        Assert.DoesNotContain("DeletedView", script.Sql);   // Delete tiki kaldırıldı → yazılmamalı
    }

    [Fact]
    public void Selecting_only_change_excludes_add_and_delete()
    {
        var (src, tgt, add, chg, del) = Mixed();
        var result = SchemaComparer.Compare(src, tgt);

        var script = ModuleScriptGenerator.Generate(result, Only(chg), ScriptOptions.Default);

        Assert.Contains(chg, script.Included);
        Assert.Contains("ALTER PROCEDURE [dbo].[ChangedProc]", script.Sql);
        Assert.DoesNotContain("AddedView", script.Sql);
        Assert.DoesNotContain("DeletedView", script.Sql);
    }

    [Fact]
    public void Selecting_only_delete_excludes_add_and_change()
    {
        var (src, tgt, add, chg, del) = Mixed();
        var result = SchemaComparer.Compare(src, tgt);

        var script = ModuleScriptGenerator.Generate(result, Only(del), ScriptOptions.Default);

        Assert.Contains(del, script.Included);
        Assert.Contains("DROP VIEW [dbo].[DeletedView]", script.Sql);
        Assert.DoesNotContain("AddedView", script.Sql);
        Assert.DoesNotContain("ChangedProc", script.Sql);
    }

    [Fact]
    public void Null_selection_writes_all_three()
    {
        var (src, tgt, _, _, _) = Mixed();
        var result = SchemaComparer.Compare(src, tgt);

        // selection = null → filtre yok → HEPSİ yazılır (API/CLI varsayılanı).
        var script = ModuleScriptGenerator.Generate(result, selection: null, ScriptOptions.Default);

        Assert.Contains("AddedView", script.Sql);
        Assert.Contains("ChangedProc", script.Sql);
        Assert.Contains("DeletedView", script.Sql);
    }

    [Fact]
    public void Empty_selection_writes_nothing()
    {
        var (src, tgt, _, _, _) = Mixed();
        var result = SchemaComparer.Compare(src, tgt);

        // Kullanıcı tüm tikleri kaldırdı → BOŞ (ama null olmayan) seçim → HİÇBİRİ yazılmaz.
        var empty = new HashSet<ObjectKey>(ObjectKeyComparer.CaseInsensitive);
        var script = ModuleScriptGenerator.Generate(result, empty, ScriptOptions.Default);

        Assert.Empty(script.Included);
        Assert.DoesNotContain("AddedView", script.Sql);
        Assert.DoesNotContain("ChangedProc", script.Sql);
        Assert.DoesNotContain("DeletedView", script.Sql);
    }

    // --- Tablolar (Add/Change/Delete ayrı kod yollarına dağılır) ---

    private static (DatabaseSnapshot src, DatabaseSnapshot tgt, ObjectKey add, ObjectKey chg, ObjectKey del) MixedTables()
    {
        var add = Table("AddedTable");
        var chg = Table("ChangedTable");
        var del = Table("DeletedTable");

        var source = Database("dev",
        [
            Obj(add, 1, displayScript: "CREATE TABLE [dbo].[AddedTable] (\n    [Id] INT NOT NULL\n);",
                columns: [Column("Id", nullable: false)]),
            Obj(chg, 2, columns: [Column("Id"), Column("NewCol", typeName: "int", nullable: true)]),
        ]);
        var target = Database("prod",
        [
            Obj(chg, 3, columns: [Column("Id")]),
            Obj(del, 4, columns: [Column("Id")]),
        ]);
        return (source, target, add, chg, del);
    }

    [Fact]
    public void Table_deselecting_change_and_delete_excludes_them_keeps_add()
    {
        var (src, tgt, add, chg, del) = MixedTables();
        var result = SchemaComparer.Compare(src, tgt);

        var script = TableScriptGenerator.Generate(result, Only(add), TableScriptOptions.Default);

        Assert.Contains(add, script.Included);
        Assert.Contains("AddedTable", script.Sql);
        Assert.DoesNotContain("ChangedTable", script.Sql);   // Change tiki kaldırıldı
        Assert.DoesNotContain("DeletedTable", script.Sql);   // Delete tiki kaldırıldı
    }

    [Fact]
    public void Table_selecting_only_delete_excludes_add_and_change()
    {
        var (src, tgt, add, chg, del) = MixedTables();
        var result = SchemaComparer.Compare(src, tgt);

        var script = TableScriptGenerator.Generate(result, Only(del),
            TableScriptOptions.Default with { IncludeDrops = true, AllowDataLoss = true });

        Assert.Contains("DeletedTable", script.Sql);
        Assert.DoesNotContain("AddedTable", script.Sql);
        Assert.DoesNotContain("ChangedTable", script.Sql);
    }
}
