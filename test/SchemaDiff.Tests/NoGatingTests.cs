using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using static SchemaDiff.Tests.TestFactory;

namespace SchemaDiff.Tests;

/// <summary>
/// "Seçtiğim her tik script'e geçsin" davranışı: AllowDataLoss açıkken (web varsayılanı artık bu)
/// riskli/bloklayan adımlar bile GERİ BIRAKILMAZ — seçilen tablonun her değişikliği yazılır.
/// AllowDataLoss kapalıyken (güvenli mod) eskisi gibi gated kalır — regresyon koruması dahil.
/// </summary>
public class NoGatingTests
{
    private static readonly TableScriptOptions WriteAll = new() { AllowDataLoss = true };

    private static TableScriptResult Gen(DatabaseSnapshot src, DatabaseSnapshot tgt, TableScriptOptions opt) =>
        TableScriptGenerator.Generate(SchemaComparer.Compare(src, tgt), null, opt);

    // 1) Dolu tabloya DEFAULT'suz NOT NULL kolon eklemek — normalde gated, artık AllowDataLoss ile yazılır.
    [Fact]
    public void NotNull_no_default_on_populated_table_is_written_when_allow_data_loss()
    {
        var key = Table("T");
        var src = Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("Req", nullable: false)])]);
        var tgt = Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 100)]);

        var script = Gen(src, tgt, WriteAll);

        Assert.Contains("ADD [Req] int NOT NULL;", script.Sql);
        Assert.Empty(script.DataLossActions);   // hiçbir adım geri bırakılmadı
    }

    // 2) NULL → NOT NULL (dolu tablo) = BlockedIfNotEmpty — "will block" durumu, artık yazılır.
    [Fact]
    public void Making_column_not_null_on_populated_table_is_written_when_allow_data_loss()
    {
        var key = Table("T");
        var src = Database("dev", [Obj(key, 1, columns: [Column("C", nullable: false)])]);
        var tgt = Database("prod", [Obj(key, 2, columns: [Column("C", nullable: true)], rowCount: 3)]);

        var script = Gen(src, tgt, WriteAll);

        Assert.Contains("ALTER COLUMN [C] int NOT NULL;", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    // 3) Tip daraltma (varchar 200 → 20) — normalde gated, artık yazılır.
    [Fact]
    public void Narrowing_column_is_written_when_allow_data_loss()
    {
        var key = Table("T");
        var src = Database("dev", [Obj(key, 1, columns: [Column("Name", typeName: "varchar", maxLength: 20)])]);
        var tgt = Database("prod", [Obj(key, 2, columns: [Column("Name", typeName: "varchar", maxLength: 200)], rowCount: 10)]);

        var script = Gen(src, tgt, WriteAll);

        Assert.Contains("ALTER COLUMN [Name] varchar(20)", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    // 4) DROP COLUMN — artık yazılır.
    [Fact]
    public void Dropping_column_is_written_when_allow_data_loss()
    {
        var key = Table("T");
        var src = Database("dev", [Obj(key, 1, columns: [Column("Id")])]);
        var tgt = Database("prod", [Obj(key, 2, columns: [Column("Id"), Column("Old")], rowCount: 10)]);

        var script = Gen(src, tgt, WriteAll);

        Assert.Contains("DROP COLUMN [Old];", script.Sql);
        Assert.Empty(script.DataLossActions);
    }

    // 5) Hepsi bir arada: NOT NULL ekle + daralt + NULL→NOT NULL + kolon sil → TÜMÜ yazılır, gated YOK.
    [Fact]
    public void All_risky_changes_are_written_together_with_nothing_gated()
    {
        var key = Table("Wide");
        var src = Database("dev", [Obj(key, 1, columns:
        [
            Column("Id"),
            Column("NewReq", nullable: false),                      // ekle: NOT NULL, DEFAULT yok
            Column("Name", typeName: "varchar", maxLength: 20),     // daralt
            Column("Flag", nullable: false),                        // NULL → NOT NULL
        ])]);
        var tgt = Database("prod", [Obj(key, 2, columns:
        [
            Column("Id"),
            Column("Name", typeName: "varchar", maxLength: 200),
            Column("Flag", nullable: true),
            Column("Dropped"),                                      // sil
        ], rowCount: 500)]);

        var script = Gen(src, tgt, WriteAll);

        Assert.Contains("ADD [NewReq] int NOT NULL;", script.Sql);
        Assert.Contains("ALTER COLUMN [Name] varchar(20)", script.Sql);
        Assert.Contains("ALTER COLUMN [Flag] int NOT NULL;", script.Sql);
        Assert.Contains("DROP COLUMN [Dropped];", script.Sql);
        Assert.Empty(script.DataLossActions);                       // hiçbiri geri bırakılmadı
    }

    // Regresyon: güvenli mod (AllowDataLoss=false) HÂLÂ geri bırakır.
    [Fact]
    public void Safe_mode_still_gates_risky_changes()
    {
        var key = Table("Wide");
        var src = Database("dev", [Obj(key, 1, columns:
        [
            Column("Id"),
            Column("NewReq", nullable: false),
            Column("Flag", nullable: false),
        ])]);
        var tgt = Database("prod", [Obj(key, 2, columns:
        [
            Column("Id"),
            Column("Flag", nullable: true),
            Column("Dropped"),
        ], rowCount: 500)]);

        var script = Gen(src, tgt, TableScriptOptions.Default);   // AllowDataLoss=false

        Assert.NotEmpty(script.DataLossActions);
        Assert.DoesNotContain("ADD [NewReq]", script.Sql);
        Assert.DoesNotContain("DROP COLUMN [Dropped]", script.Sql);
    }
}
