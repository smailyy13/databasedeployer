using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Canlı dev→prod review turunda doğrulanan davranışları kalıcılaştıran uçtan uca
/// (SnapshotBuilder → SchemaComparer / TableScriptGenerator) edge-case testleri:
/// harf-yalnızca farkların yok sayılması, CHECK boşluğu, computed ifade case'i,
/// prod-only DEFAULT düşürme ve FK bağımlılık sırası (Parent önce Child).
/// </summary>
public class EdgeCaseReviewTests
{
    private static readonly SnapshotOptions Opts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    // 19 pozisyonlu ColumnRow için kısa yol. int kolon; opsiyonel computed/default alanları.
    private static ColumnRow Col(int oid, int cid, string name,
        bool computed = false, string? computedDef = null,
        string? defName = null, string? defDef = null) =>
        new(oid, cid, name, "sys", "int", 4, 10, 0, true, null, false, computed,
            defName, defDef, defName is null ? (bool?)null : false,
            computedDef, computed ? false : (bool?)null, null, null);

    private static ObjectRow TableObj(int oid, string name) =>
        new(oid, "dbo", name, "U", DateTime.UnixEpoch, 0);

    private static DatabaseSnapshot Build(CatalogSet c, SnapshotOptions? o = null) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), o ?? Opts);

    private static CatalogSet Empty() => new() { DatabaseName = "t", ServerName = "S" };

    // ---- 1) Harf-yalnızca kolon adı farkı ----

    private static CatalogSet CustTable(string emailCol) => new()
    {
        DatabaseName = "t", ServerName = "S",
        Objects = [TableObj(100, "Cust")],
        Columns = [Col(100, 1, "Id"), Col(100, 2, emailCol)],
    };

    [Fact]
    public void Case_only_column_name_is_not_a_difference()
    {
        var src = Build(CustTable("Email"));
        var tgt = Build(CustTable("email"));
        Assert.Empty(SchemaComparer.Compare(src, tgt).Differences);
    }

    [Fact]
    public void Case_only_column_name_is_reported_when_case_sensitive()
    {
        var cs = SnapshotOptions.Default with { KeepDisplayScripts = true, CaseSensitiveColumnNames = true };
        var src = Build(CustTable("Email"), cs);
        var tgt = Build(CustTable("email"), cs);
        Assert.NotEmpty(SchemaComparer.Compare(src, tgt).Differences);
    }

    // ---- 2) CHECK constraint'te boşluk farkı yok sayılır ----

    private static CatalogSet AgeCheck(string definition) => new()
    {
        DatabaseName = "t", ServerName = "S",
        Objects = [TableObj(100, "Cust")],
        Columns = [Col(100, 1, "Id"), Col(100, 2, "Age")],
        CheckConstraints = [new CheckConstraintRow(100, "CK_Cust_Age", false, definition, false, false)],
    };

    [Fact]
    public void Check_constraint_whitespace_is_ignored()
    {
        var src = Build(AgeCheck("([Age]>0)"));
        var tgt = Build(AgeCheck("([Age] > 0)"));
        Assert.Empty(SchemaComparer.Compare(src, tgt).Differences);
    }

    // ---- 3) Computed kolon ifadesinde harf-yalnızca fark yok sayılır ----

    private static CatalogSet Computed(string expr) => new()
    {
        DatabaseName = "t", ServerName = "S",
        Objects = [TableObj(100, "Cust")],
        Columns = [Col(100, 1, "Id"), Col(100, 2, "Val"), Col(100, 3, "FullCol", computed: true, computedDef: expr)],
    };

    [Fact]
    public void Computed_column_case_only_expression_is_ignored()
    {
        var src = Build(Computed("[Val]"));
        var tgt = Build(Computed("[val]"));
        Assert.Empty(SchemaComparer.Compare(src, tgt).Differences);
    }

    // ---- 4) Prod-only DEFAULT constraint düşürülür ----

    private static CatalogSet WithStatusDefault(bool withDefault) => new()
    {
        DatabaseName = "t", ServerName = "S",
        Objects = [TableObj(100, "Cust")],
        Columns =
        [
            Col(100, 1, "Id"),
            withDefault
                ? Col(100, 2, "Status", defName: "DF_Cust_Status", defDef: "((0))")
                : Col(100, 2, "Status"),
        ],
    };

    [Fact]
    public void Prod_only_default_constraint_is_dropped()
    {
        // Kaynakta default YOK, hedefte VAR → hedefi kaynağa eşitlemek için DROP üretilmeli.
        var source = Build(WithStatusDefault(false));
        var target = Build(WithStatusDefault(true));
        var sql = TableScriptGenerator.Generate(
            SchemaComparer.Compare(source, target), null,
            TableScriptOptions.Default with { IncludeDrops = true }).Sql;
        Assert.Contains("DROP CONSTRAINT [DF_Cust_Status]", sql);
    }

    // ---- 5) FK bağımlılık sırası: referans edilen tablo önce yaratılır ----

    private static CatalogSet ParentChild() => new()
    {
        DatabaseName = "t", ServerName = "S",
        Objects = [TableObj(100, "Parent"), TableObj(101, "Child")],
        Columns =
        [
            Col(100, 1, "Id"),
            Col(101, 1, "Id"), Col(101, 2, "ParentId"),
        ],
        ForeignKeys = [new ForeignKeyRow(500, 101, "FK_Child_Parent", false, 100, 0, 0, false, false)],
        ForeignKeyColumns = [new ForeignKeyColumnRow(500, 1, 101, 2, 100, 1)],
    };

    [Fact]
    public void Foreign_key_dependency_creates_parent_before_child()
    {
        var sql = TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(ParentChild()), Build(Empty())), null,
            TableScriptOptions.Default).Sql;

        var parentAt = sql.IndexOf("CREATE TABLE [dbo].[Parent]", StringComparison.Ordinal);
        var childAt = sql.IndexOf("CREATE TABLE [dbo].[Child]", StringComparison.Ordinal);
        Assert.True(parentAt >= 0 && childAt >= 0, "Her iki tablo da üretilmeli");
        Assert.True(parentAt < childAt, "Parent, Child'dan önce yaratılmalı (FK bağımlılığı)");
    }
}
