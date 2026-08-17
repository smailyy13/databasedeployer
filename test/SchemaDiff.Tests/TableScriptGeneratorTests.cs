using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Tests.TestFactory;

namespace SchemaDiff.Tests;

public class TableScriptGeneratorTests
{
    private static TableScriptResult Generate(
        DatabaseSnapshot source, DatabaseSnapshot target,
        TableScriptOptions? options = null, ISet<ObjectKey>? selection = null)
    {
        var result = SchemaComparer.Compare(source, target);
        return TableScriptGenerator.Generate(result, selection, options ?? TableScriptOptions.Default);
    }

    [Fact]
    public void Added_table_emits_its_create_script()
    {
        var key = Table("Orders");
        var source = Database("dev",
            [Obj(key, 1, displayScript: "CREATE TABLE [dbo].[Orders] (\n    [Id] INT NOT NULL\n);",
                columns: [Column("Id", nullable: false)])]);
        var target = Database("prod", []);

        var script = Generate(source, target);

        Assert.Contains(key, script.Included);
        Assert.Contains("CREATE TABLE [dbo].[Orders]", script.Sql);
    }

    [Fact]
    public void Adding_nullable_column_is_emitted_directly()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("Note", typeName: "nvarchar", maxLength: 200, nullable: true)])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 5000)]);

        var script = Generate(source, target);

        Assert.Contains(key, script.Included);
        Assert.Contains("ALTER TABLE [dbo].[T] ADD [Note] nvarchar(100) NULL;", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Column_case_only_change_emits_sp_rename()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1,
            columns: [Column("Id"), Column("TranDate", typeName: "date")])]);
        var target = Database("prod", [Obj(key, 2,
            columns: [Column("Id"), Column("Trandate", typeName: "date")], rowCount: 1000)]);

        var script = Generate(source, target);

        Assert.Contains(key, script.Included);
        Assert.Contains("EXEC sp_rename N'[dbo].[T].[Trandate]', N'TranDate', N'COLUMN';", script.Sql);
        // Yeniden adlandırma güvenli: veri kaybı yok, gereksiz ALTER COLUMN da yok.
        Assert.Empty(script.DataLossActions);
        Assert.DoesNotContain("ALTER COLUMN", script.Sql);
    }

    [Fact]
    public void Identical_column_case_does_not_emit_sp_rename()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1,
            columns: [Column("Id"), Column("Note", typeName: "nvarchar", maxLength: 200)])]);
        var target = Database("prod", [Obj(key, 2,
            columns: [Column("Id")])]);

        var script = Generate(source, target);

        Assert.DoesNotContain("sp_rename", script.Sql);
    }

    [Fact]
    public void Adding_not_null_column_without_default_to_populated_table_is_gated()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("Req", nullable: false)])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 100)]);

        var script = Generate(source, target);

        Assert.DoesNotContain("ADD [Req]", script.Sql);
        Assert.Contains(script.DataLossActions, a => a.Column == "Req");
    }

    [Fact]
    public void Adding_not_null_column_to_empty_table_is_emitted()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("Req", nullable: false)])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 0)]);

        var script = Generate(source, target);

        Assert.Contains("ADD [Req] int NOT NULL;", script.Sql);
    }

    [Fact]
    public void Dropping_column_is_gated_by_default()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Id")])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Id"), Column("Old")], rowCount: 10)]);

        var script = Generate(source, target);

        Assert.DoesNotContain("DROP COLUMN [Old]", script.Sql);
        Assert.Contains(script.DataLossActions, a => a.Column == "Old");
    }

    [Fact]
    public void Dropping_column_is_emitted_when_data_loss_allowed()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Id")])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Id"), Column("Old")], rowCount: 10)]);

        var script = Generate(source, target, new TableScriptOptions { AllowDataLoss = true });

        Assert.Contains("ALTER TABLE [dbo].[T] DROP COLUMN [Old];", script.Sql);
    }

    [Fact]
    public void Widening_column_is_emitted_as_alter()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Name", typeName: "varchar", maxLength: 200)])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Name", typeName: "varchar", maxLength: 50)], rowCount: 10)]);

        var script = Generate(source, target);

        Assert.Contains("ALTER TABLE [dbo].[T] ALTER COLUMN [Name] varchar(200) NULL;", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    [Fact]
    public void Narrowing_column_is_gated_by_default()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Name", typeName: "varchar", maxLength: 20)])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Name", typeName: "varchar", maxLength: 200)], rowCount: 10)]);

        var script = Generate(source, target);

        Assert.DoesNotContain("ALTER COLUMN [Name]", script.Sql);
        Assert.Contains(script.DataLossActions, a => a.Column == "Name");
    }

    [Fact]
    public void Making_column_not_null_on_populated_table_is_gated()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("C", nullable: false)])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("C", nullable: true)], rowCount: 3)]);

        var script = Generate(source, target);

        Assert.DoesNotContain("ALTER COLUMN [C]", script.Sql);
        Assert.Contains(script.DataLossActions, a => a.Column == "C");
    }

    [Fact]
    public void Identity_change_is_skipped_not_altered()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Id", identity: true)])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Id", identity: false)], rowCount: 10)]);

        var script = Generate(source, target);

        Assert.DoesNotContain("ALTER COLUMN [Id]", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == key);
    }

    [Fact]
    public void Removed_table_is_gated_by_default()
    {
        var key = Table("Gone");
        var source = Database("dev", []);
        var target = Database("prod", [Obj(key, 1, columns: [Column("Id")], rowCount: 999)]);

        var script = Generate(source, target);

        Assert.DoesNotContain("DROP TABLE", script.Sql);
        Assert.Contains(script.DataLossActions, a => a.Table == key);
    }

    [Fact]
    public void Removed_table_is_emitted_when_data_loss_allowed()
    {
        var key = Table("Gone");
        var source = Database("dev", []);
        var target = Database("prod", [Obj(key, 1, columns: [Column("Id")], rowCount: 999)]);

        var script = Generate(source, target, new TableScriptOptions { AllowDataLoss = true });

        Assert.Contains("DROP TABLE [dbo].[Gone];", script.Sql);
        Assert.Contains("OBJECT_ID(N'[dbo].[Gone]', N'U')", script.Sql);
    }

    [Fact]
    public void Non_column_change_only_is_reported_as_manual_review()
    {
        // Kolonlar aynı ama hash farklı (index değişmiş) → kolon üretimi yok, elle gözden geçir.
        var key = Table("T");
        var src = Obj(key, 1, columns: [Column("Id")]);
        src.Parts["indexes"] = 5;
        var tgt = Obj(key, 2, columns: [Column("Id")], rowCount: 10);
        tgt.Parts["indexes"] = 9;

        var script = Generate(Database("dev", [src]), Database("prod", [tgt]));

        Assert.DoesNotContain(key, script.Included);
        Assert.Contains(script.Skipped, s => s.Key == key && s.Reason.Contains("elle gözden geçirin"));
    }

    [Fact]
    public void Modules_are_ignored_only_tables_handled()
    {
        var view = View("V");
        var table = Table("T");
        var source = Database("dev",
        [
            Obj(view, 1, displayScript: "CREATE VIEW [dbo].[V] AS SELECT 1 AS x"),
            Obj(table, 1, displayScript: "CREATE TABLE [dbo].[T] ([Id] INT NOT NULL);", columns: [Column("Id", nullable: false)]),
        ]);
        var target = Database("prod", []);

        var script = Generate(source, target);

        Assert.Contains(table, script.Included);
        Assert.DoesNotContain("CREATE VIEW", script.Sql);
    }

    [Fact]
    public void Column_order_only_difference_is_reported_specifically()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("A"), Column("B"), Column("C")])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("A"), Column("C"), Column("B")], rowCount: 10)]);

        var script = Generate(source, target);

        Assert.DoesNotContain("ALTER TABLE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key == key && s.Reason.Contains("kolon SIRASI"));
    }

    [Fact]
    public void Selection_limits_to_chosen_tables()
    {
        var wanted = Table("Wanted");
        var other = Table("Other");
        var source = Database("dev",
        [
            Obj(wanted, 1, displayScript: "CREATE TABLE [dbo].[Wanted] ([Id] INT NOT NULL);", columns: [Column("Id", nullable: false)]),
            Obj(other, 1, displayScript: "CREATE TABLE [dbo].[Other] ([Id] INT NOT NULL);", columns: [Column("Id", nullable: false)]),
        ]);
        var target = Database("prod", []);
        var selection = new HashSet<ObjectKey>(ObjectKeyComparer.CaseInsensitive) { wanted };

        var script = Generate(source, target, selection: selection);

        Assert.Contains(wanted, script.Included);
        Assert.DoesNotContain("Other", script.Sql);
    }
}
