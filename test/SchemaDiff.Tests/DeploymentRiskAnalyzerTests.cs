using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using static SchemaDiff.Tests.TestFactory;

namespace SchemaDiff.Tests;

public class DeploymentRiskAnalyzerTests
{
    /// <summary>Aynı tabloyu iki kolon setiyle karşılaştırıp risk üretir.</summary>
    private static TableRisk RiskFor(
        IReadOnlyList<ColumnInfo> sourceCols,
        IReadOnlyList<ColumnInfo> targetCols,
        long targetRows)
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: sourceCols)]);
        var target = Database("prod", [Obj(key, 2, columns: targetCols, rowCount: targetRows)]);
        var result = SchemaComparer.Compare(source, target);
        return Assert.Single(DeploymentRiskAnalyzer.Analyze(result));
    }

    [Fact]
    public void Dropped_column_is_DataLoss()
    {
        var risk = RiskFor(
            sourceCols: [Column("Keep")],
            targetCols: [Column("Keep"), Column("Removed")],
            targetRows: 100);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
        Assert.Contains(risk.Findings, f => f.Column == "Removed" && f.Risk == DeploymentRisk.DataLoss);
    }

    [Fact]
    public void Adding_not_null_column_without_default_blocks_when_table_has_rows()
    {
        var risk = RiskFor(
            sourceCols: [Column("Id"), Column("NewReq", nullable: false, defaultDefinition: null)],
            targetCols: [Column("Id")],
            targetRows: 5000);

        Assert.Equal(DeploymentRisk.BlockedIfNotEmpty, risk.Risk);
        Assert.True(risk.WillBlock);
    }

    [Fact]
    public void Adding_not_null_column_without_default_does_not_block_when_table_is_empty()
    {
        var risk = RiskFor(
            sourceCols: [Column("Id"), Column("NewReq", nullable: false)],
            targetCols: [Column("Id")],
            targetRows: 0);

        Assert.Equal(DeploymentRisk.BlockedIfNotEmpty, risk.Risk);
        // Risk sınıfı aynı ama boş tabloda deployment durmaz.
        Assert.False(risk.WillBlock);
    }

    [Fact]
    public void Adding_nullable_column_is_Safe()
    {
        var risk = RiskFor(
            sourceCols: [Column("Id"), Column("Note", typeName: "nvarchar", nullable: true)],
            targetCols: [Column("Id")],
            targetRows: 5000);

        Assert.Equal(DeploymentRisk.Safe, risk.Risk);
        Assert.False(risk.WillBlock);
    }

    [Fact]
    public void Widening_varchar_length_is_InPlace()
    {
        var risk = RiskFor(
            sourceCols: [Column("Name", typeName: "varchar", maxLength: 100)],
            targetCols: [Column("Name", typeName: "varchar", maxLength: 50)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.InPlace, risk.Risk);
    }

    [Fact]
    public void Narrowing_varchar_length_is_DataLoss()
    {
        var risk = RiskFor(
            sourceCols: [Column("Name", typeName: "varchar", maxLength: 20)],
            targetCols: [Column("Name", typeName: "varchar", maxLength: 200)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
    }

    [Fact]
    public void Making_nullable_column_not_null_blocks_if_not_empty()
    {
        var risk = RiskFor(
            sourceCols: [Column("C", nullable: false)],
            targetCols: [Column("C", nullable: true)],
            targetRows: 3);

        Assert.Equal(DeploymentRisk.BlockedIfNotEmpty, risk.Risk);
        Assert.True(risk.WillBlock);
    }

    [Fact]
    public void Int_to_bigint_widening_is_InPlace()
    {
        var risk = RiskFor(
            sourceCols: [Column("N", typeName: "bigint", maxLength: 8)],
            targetCols: [Column("N", typeName: "int", maxLength: 4)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.InPlace, risk.Risk);
    }

    [Fact]
    public void Bigint_to_int_narrowing_is_DataLoss()
    {
        var risk = RiskFor(
            sourceCols: [Column("N", typeName: "int", maxLength: 4)],
            targetCols: [Column("N", typeName: "bigint", maxLength: 8)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
    }

    [Fact]
    public void Dropped_table_is_DataLoss()
    {
        var key = Table("Gone");
        var source = Database("dev", []);
        var target = Database("prod", [Obj(key, 1, rowCount: 999)]);
        var result = SchemaComparer.Compare(source, target);

        var risk = Assert.Single(DeploymentRiskAnalyzer.Analyze(result));
        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
        Assert.True(risk.WillBlock);
    }

    [Fact]
    public void Changed_table_with_no_column_risk_still_appears_in_report()
    {
        // Sadece index değişen tablo raporda kalmalı — sessizce düşmesi yanıltır.
        var key = Table("T");
        var src = Obj(key, 1, columns: [Column("Id")]);
        src.Parts["indexes"] = 5;
        var tgt = Obj(key, 2, columns: [Column("Id")], rowCount: 1000);
        tgt.Parts["indexes"] = 9;
        var result = SchemaComparer.Compare(Database("dev", [src]), Database("prod", [tgt]));

        var risk = Assert.Single(DeploymentRiskAnalyzer.Analyze(result));
        Assert.Equal(DeploymentRisk.Safe, risk.Risk);
        Assert.NotEmpty(risk.Findings);
    }

    // --- constraint eklemeleri (kolon dışı) ---

    /// <summary>Constraint senaryosu kurar: kaynakta olan/olmayan index/check/fk ile risk üretir.</summary>
    private static TableRisk ConstraintRisk(
        long targetRows,
        IReadOnlyList<ColumnInfo>? cols = null,
        string? srcIndexes = null, string? tgtIndexes = null,
        string? srcChecks = null, string? tgtChecks = null,
        string? srcFks = null, string? tgtFks = null)
    {
        cols ??= [Column("Id")];
        var key = Table("T");
        var source = Database("dev",
            [Obj(key, 1, columns: cols, indexes: srcIndexes, checks: srcChecks, foreignKeys: srcFks)]);
        var target = Database("prod",
            [Obj(key, 2, columns: cols, rowCount: targetRows, indexes: tgtIndexes, checks: tgtChecks, foreignKeys: tgtFks)]);
        return Assert.Single(DeploymentRiskAnalyzer.Analyze(SchemaComparer.Compare(source, target)));
    }

    [Fact]
    public void Adding_unique_index_blocks_when_table_has_rows()
    {
        var risk = ConstraintRisk(
            targetRows: 5000,
            srcIndexes: "idx|UQ_Email|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Email ASC|include=",
            tgtIndexes: null);

        Assert.Equal(DeploymentRisk.BlockedIfNotEmpty, risk.Risk);
        Assert.True(risk.WillBlock);
        Assert.Contains(risk.Findings, f => f.Column == "UQ_Email");
    }

    [Fact]
    public void Adding_nonunique_index_is_InPlace_not_blocking()
    {
        var risk = ConstraintRisk(
            targetRows: 5000,
            srcIndexes: "idx|IX_Name|NONCLUSTERED|unique=0|pk=0|uq=0|keys=Name ASC|include=",
            tgtIndexes: null);

        Assert.Equal(DeploymentRisk.InPlace, risk.Risk);
        Assert.False(risk.WillBlock);
    }

    [Fact]
    public void Adding_check_constraint_blocks_when_table_has_rows()
    {
        var risk = ConstraintRisk(
            targetRows: 42,
            srcChecks: "chk|CK_Age|([Age]>(0))|disabled=0|notTrusted=0",
            tgtChecks: null);

        Assert.Equal(DeploymentRisk.BlockedIfNotEmpty, risk.Risk);
        Assert.True(risk.WillBlock);
    }

    [Fact]
    public void Adding_foreign_key_blocks_when_table_has_rows()
    {
        var risk = ConstraintRisk(
            targetRows: 7,
            srcFks: "fk|FK_T_P|ref=[dbo].[P]|cols=PId>Id|onDelete=0|onUpdate=0|disabled=0|notTrusted=0",
            tgtFks: null);

        Assert.Equal(DeploymentRisk.BlockedIfNotEmpty, risk.Risk);
        Assert.True(risk.WillBlock);
    }

    [Fact]
    public void Constraint_present_in_both_is_not_reported_as_added()
    {
        // Yalnızca fiziksel opsiyon (fill factor) farkı — imza aynı, "eklendi" sayılmamalı.
        var risk = ConstraintRisk(
            targetRows: 5000,
            srcIndexes: "idx|UQ_Email|NONCLUSTERED|unique=1|pk=0|uq=1|fill=90|keys=Email ASC|include=",
            tgtIndexes: "idx|UQ_Email|NONCLUSTERED|unique=1|pk=0|uq=1|fill=0|keys=Email ASC|include=");

        Assert.Equal(DeploymentRisk.Safe, risk.Risk);
        Assert.False(risk.WillBlock);
        Assert.DoesNotContain(risk.Findings, f => f.Risk == DeploymentRisk.BlockedIfNotEmpty);
    }

    // --- yeni tablo, kolon sırası, default, büyük tablo, bilinmeyen satır sayısı ---

    [Fact]
    public void Added_table_appears_as_Safe()
    {
        var key = Table("New");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("Name")])]);
        var target = Database("prod", []);

        var risk = Assert.Single(DeploymentRiskAnalyzer.Analyze(SchemaComparer.Compare(source, target)));
        Assert.Equal(DeploymentRisk.Safe, risk.Risk);
        Assert.False(risk.WillBlock);
    }

    [Fact]
    public void Column_reorder_is_flagged()
    {
        var risk = RiskFor(
            sourceCols: [Column("A"), Column("B")],
            targetCols: [Column("B"), Column("A")],
            targetRows: 10);

        Assert.Contains(risk.Findings, f => f.Column == "(kolon sırası)");
    }

    [Fact]
    public void Default_change_is_reported_as_Safe()
    {
        var risk = RiskFor(
            sourceCols: [Column("C", nullable: false, defaultDefinition: "(1)")],
            targetCols: [Column("C", nullable: false, defaultDefinition: "(0)")],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.Safe, risk.Risk);
        Assert.Contains(risk.Findings, f => f.Description.Contains("DEFAULT"));
    }

    [Fact]
    public void Large_table_gets_performance_warning_on_inplace_change()
    {
        var risk = RiskFor(
            sourceCols: [Column("Name", typeName: "varchar", maxLength: 100)],
            targetCols: [Column("Name", typeName: "varchar", maxLength: 50)],
            targetRows: 2_000_000);

        Assert.Equal(DeploymentRisk.InPlace, risk.Risk);
        Assert.Contains(risk.Findings, f => f.Column == "(performans)");
    }

    [Fact]
    public void Blocking_change_with_unknown_row_count_still_blocks()
    {
        var key = Table("T");
        var source = Database("dev",
            [Obj(key, 1, columns: [Column("Id"), Column("Req", nullable: false)])]);
        var target = Database("prod",
            [Obj(key, 2, columns: [Column("Id")], rowCount: null)]);

        var risk = Assert.Single(DeploymentRiskAnalyzer.Analyze(SchemaComparer.Compare(source, target)));
        Assert.Equal(DeploymentRisk.BlockedIfNotEmpty, risk.Risk);
        Assert.True(risk.WillBlock);
    }

    // --- A: kolon sırası "yok say" seçeneğine saygı ---

    [Fact]
    public void Column_reorder_is_NOT_flagged_when_ignoreColumnOrder_is_on()
    {
        var key = Table("T");
        // Kullanıcı "kolon sırasını yok say" dedi ama tablo başka bir sebeple değişmiş.
        var source = Database("dev", [Obj(key, 1, columns: [Column("A"), Column("B")])],
            ignoredColumnOrder: true);
        var target = Database("prod", [Obj(key, 2, columns: [Column("B"), Column("A")], rowCount: 10)],
            ignoredColumnOrder: true);

        var risk = Assert.Single(DeploymentRiskAnalyzer.Analyze(SchemaComparer.Compare(source, target)));
        Assert.DoesNotContain(risk.Findings, f => f.Column == "(kolon sırası)");
    }

    // --- B: collation "yok say" seçeneğine saygı ---

    [Fact]
    public void Collation_change_is_NOT_flagged_when_ignoreCollation_is_on()
    {
        var key = Table("T");
        var source = Database("dev", [Obj(key, 1, columns: [Column("C", collation: "Turkish_CI_AS")])],
            ignoredCollation: true);
        var target = Database("prod", [Obj(key, 2, columns: [Column("C", collation: "Latin1_General_CI_AS")], rowCount: 10)],
            ignoredCollation: true);

        var risk = Assert.Single(DeploymentRiskAnalyzer.Analyze(SchemaComparer.Compare(source, target)));
        Assert.DoesNotContain(risk.Findings, f => f.Description.Contains("Collation"));
    }

    [Fact]
    public void Collation_change_IS_flagged_by_default()
    {
        var risk = RiskFor(
            sourceCols: [Column("C", collation: "Turkish_CI_AS")],
            targetCols: [Column("C", collation: "Latin1_General_CI_AS")],
            targetRows: 10);

        Assert.Contains(risk.Findings, f => f.Description.Contains("Collation"));
    }

    // --- C: koşullu (kontrol et) vs kesin blok ayrımı ---

    [Fact]
    public void Unique_constraint_add_is_conditional_only()
    {
        var risk = ConstraintRisk(
            targetRows: 5000,
            srcIndexes: "idx|UQ_Email|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Email ASC|include=");

        Assert.True(risk.WillBlock);
        Assert.True(risk.ConditionalOnly);   // veri temizse aslında geçer → amber "KONTROL ET"
    }

    [Fact]
    public void Not_null_column_without_default_is_certain_block()
    {
        var risk = RiskFor(
            sourceCols: [Column("Id"), Column("NewReq", nullable: false, defaultDefinition: null)],
            targetCols: [Column("Id")],
            targetRows: 5000);

        Assert.True(risk.WillBlock);
        Assert.False(risk.ConditionalOnly);  // DEFAULT'suz NOT NULL → kesin başarısız
    }

    [Fact]
    public void Mixed_certain_and_conditional_is_not_conditional_only()
    {
        var key = Table("T");
        // Kaynakta hem DEFAULT'suz NOT NULL kolon (kesin blok) hem UNIQUE index (koşullu) ekleniyor.
        var source = Database("dev", [Obj(key, 1,
            columns: [Column("Id"), Column("NewReq", nullable: false)],
            indexes: "idx|UQ_Email|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Email ASC|include=")]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 5000)]);

        var risk = Assert.Single(DeploymentRiskAnalyzer.Analyze(SchemaComparer.Compare(source, target)));
        Assert.True(risk.WillBlock);
        Assert.False(risk.ConditionalOnly);   // en az bir kesin blok var → koşullu-değil
    }

    // --- E: kaldırılan constraint güvenli olarak raporlanır ---

    [Fact]
    public void Dropped_unique_constraint_is_reported_as_Safe()
    {
        var risk = ConstraintRisk(
            targetRows: 5000,
            srcIndexes: null,
            tgtIndexes: "idx|UQ_Email|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Email ASC|include=");

        Assert.False(risk.WillBlock);
        Assert.Contains(risk.Findings, f => f.Column == "UQ_Email" && f.Risk == DeploymentRisk.Safe);
    }

    // --- constraint ekleme çeşitleri ---

    [Fact]
    public void Adding_primary_key_is_conditional_block()
    {
        var risk = ConstraintRisk(
            targetRows: 100,
            srcIndexes: "idx|PK_T|CLUSTERED|unique=1|pk=1|uq=0|keys=Id ASC|include=");

        Assert.True(risk.WillBlock);
        Assert.True(risk.ConditionalOnly);
        Assert.Contains(risk.Findings, f => f.Description.Contains("PRIMARY KEY"));
    }

    [Fact]
    public void Foreign_key_add_is_conditional_only()
    {
        var risk = ConstraintRisk(
            targetRows: 100,
            srcFks: "fk|FK_T_P|ref=[dbo].[P]|cols=PId>Id|onDelete=0|onUpdate=0|disabled=0|notTrusted=0");

        Assert.True(risk.WillBlock);
        Assert.True(risk.ConditionalOnly);
    }

    [Fact]
    public void Structurally_identical_index_with_different_name_is_not_added()
    {
        // Sistem-adlı ya da elle yeniden adlandırılmış ama aynı yapıdaki UNIQUE — "eklendi" sayılmamalı.
        var risk = ConstraintRisk(
            targetRows: 5000,
            srcIndexes: "idx|UQ_A|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Email ASC|include=",
            tgtIndexes: "idx|UQ_B|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Email ASC|include=");

        Assert.False(risk.WillBlock);
        Assert.DoesNotContain(risk.Findings, f => f.Risk == DeploymentRisk.BlockedIfNotEmpty);
    }

    [Fact]
    public void Unique_index_with_changed_keys_is_flagged_as_added()
    {
        var risk = ConstraintRisk(
            targetRows: 5000,
            srcIndexes: "idx|IX|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Email ASC|include=",
            tgtIndexes: "idx|IX|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Phone ASC|include=");

        Assert.True(risk.WillBlock);
        Assert.True(risk.ConditionalOnly);
    }

    [Fact]
    public void Check_definition_with_equals_sign_is_parsed()
    {
        // Tanımın içindeki '=' generic ayrıştırıcıyı yanıltmamalı.
        var risk = ConstraintRisk(
            targetRows: 10,
            srcChecks: "chk|CK_Status|([Status]='A')|disabled=0|notTrusted=0");

        Assert.True(risk.WillBlock);
        Assert.Contains(risk.Findings, f => f.Column == "CK_Status");
    }

    // --- tip değişimi çeşitleri ---

    [Fact]
    public void Varchar_to_nvarchar_is_InPlace_widening()
    {
        var risk = RiskFor(
            sourceCols: [Column("N", typeName: "nvarchar", maxLength: 100)],
            targetCols: [Column("N", typeName: "varchar", maxLength: 50)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.InPlace, risk.Risk);
    }

    [Fact]
    public void Nvarchar_to_varchar_is_DataLoss()
    {
        var risk = RiskFor(
            sourceCols: [Column("N", typeName: "varchar", maxLength: 50)],
            targetCols: [Column("N", typeName: "nvarchar", maxLength: 100)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
    }

    [Fact]
    public void Widen_to_max_is_InPlace()
    {
        var risk = RiskFor(
            sourceCols: [Column("N", typeName: "varchar", maxLength: -1)],
            targetCols: [Column("N", typeName: "varchar", maxLength: 100)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.InPlace, risk.Risk);
    }

    [Fact]
    public void Narrow_from_max_is_DataLoss()
    {
        var risk = RiskFor(
            sourceCols: [Column("N", typeName: "varchar", maxLength: 100)],
            targetCols: [Column("N", typeName: "varchar", maxLength: -1)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
    }

    [Fact]
    public void Decimal_precision_increase_is_InPlace()
    {
        var risk = RiskFor(
            sourceCols: [Column("D", typeName: "decimal", maxLength: 9, precision: 15, scale: 2)],
            targetCols: [Column("D", typeName: "decimal", maxLength: 9, precision: 10, scale: 2)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.InPlace, risk.Risk);
    }

    [Fact]
    public void Decimal_scale_decrease_is_DataLoss()
    {
        var risk = RiskFor(
            sourceCols: [Column("D", typeName: "decimal", maxLength: 9, precision: 12, scale: 1)],
            targetCols: [Column("D", typeName: "decimal", maxLength: 9, precision: 12, scale: 4)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
    }

    [Fact]
    public void Identity_toggle_is_certain_DataLoss()
    {
        var risk = RiskFor(
            sourceCols: [Column("Id", identity: true)],
            targetCols: [Column("Id", identity: false)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
        Assert.False(risk.ConditionalOnly);
    }

    [Fact]
    public void Computed_change_is_DataLoss()
    {
        var risk = RiskFor(
            sourceCols: [Column("C", computed: true)],
            targetCols: [Column("C", computed: false)],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
    }

    [Fact]
    public void Nullable_to_not_null_is_conditional_only()
    {
        var risk = RiskFor(
            sourceCols: [Column("C", nullable: false)],
            targetCols: [Column("C", nullable: true)],
            targetRows: 10);

        Assert.True(risk.WillBlock);
        Assert.True(risk.ConditionalOnly);
    }

    // --- performans uyarısı sınırları ---

    [Fact]
    public void Small_table_gets_no_performance_warning()
    {
        var risk = RiskFor(
            sourceCols: [Column("Name", typeName: "varchar", maxLength: 100)],
            targetCols: [Column("Name", typeName: "varchar", maxLength: 50)],
            targetRows: 10);

        Assert.DoesNotContain(risk.Findings, f => f.Column == "(performans)");
    }

    [Fact]
    public void Large_table_dataloss_only_gets_no_performance_warning()
    {
        // Perf uyarısı yalnızca InPlace (yerinde) değişimlerde eklenir, veri kaybında değil.
        var risk = RiskFor(
            sourceCols: [Column("Name", typeName: "varchar", maxLength: 20)],
            targetCols: [Column("Name", typeName: "varchar", maxLength: 200)],
            targetRows: 5_000_000);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
        Assert.DoesNotContain(risk.Findings, f => f.Column == "(performans)");
    }

    // --- birden çok bulgu / sıralama ---

    [Fact]
    public void Table_overall_risk_is_max_of_findings()
    {
        // Bir güvenli ekleme + bir kolon silme → tablo geneli DataLoss.
        var risk = RiskFor(
            sourceCols: [Column("Id"), Column("New", nullable: true)],
            targetCols: [Column("Id"), Column("Old")],
            targetRows: 10);

        Assert.Equal(DeploymentRisk.DataLoss, risk.Risk);
    }

    [Fact]
    public void Blocking_tables_are_ordered_first()
    {
        var blocK = Table("Blocker");   // dolu + bloklayan
        var safeK = Table("Safe");      // güvenli ekleme
        var source = Database("dev", [
            Obj(blocK, 1, columns: [Column("Id"), Column("Req", nullable: false)]),
            Obj(safeK, 1, columns: [Column("Id"), Column("Note", nullable: true)]),
        ]);
        var target = Database("prod", [
            Obj(blocK, 2, columns: [Column("Id")], rowCount: 1000),
            Obj(safeK, 2, columns: [Column("Id")], rowCount: 1000),
        ]);

        var risks = DeploymentRiskAnalyzer.Analyze(SchemaComparer.Compare(source, target));
        Assert.Equal("Blocker", risks[0].Table.Name);
        Assert.True(risks[0].WillBlock);
    }

    [Fact]
    public void Empty_table_blocking_change_does_not_will_block()
    {
        var risk = ConstraintRisk(
            targetRows: 0,
            srcIndexes: "idx|UQ_Email|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Email ASC|include=");

        Assert.Equal(DeploymentRisk.BlockedIfNotEmpty, risk.Risk);
        Assert.False(risk.WillBlock);        // boş tabloda constraint sorunsuz eklenir
        Assert.False(risk.ConditionalOnly);  // WillBlock değilse ConditionalOnly de değil
    }

    // --- güvenli sayılması gereken ama daha önce test edilmemiş senaryolar ---

    [Fact]
    public void Adding_not_null_column_WITH_default_is_Safe()
    {
        var risk = RiskFor(
            sourceCols: [Column("Id"), Column("Flag", nullable: false, defaultDefinition: "((0))")],
            targetCols: [Column("Id")],
            targetRows: 5000);

        Assert.Equal(DeploymentRisk.Safe, risk.Risk);
        Assert.False(risk.WillBlock);
    }

    [Fact]
    public void Adding_computed_column_is_Safe()
    {
        var risk = RiskFor(
            sourceCols: [Column("Id"), Column("Total", computed: true, nullable: false)],
            targetCols: [Column("Id")],
            targetRows: 5000);

        Assert.Equal(DeploymentRisk.Safe, risk.Risk);
    }

    [Fact]
    public void Relaxing_not_null_to_nullable_is_Safe()
    {
        // NOT NULL -> NULL gevşetmesi asla başarısız olmaz, veri kaybettirmez.
        var risk = RiskFor(
            sourceCols: [Column("C", nullable: true)],
            targetCols: [Column("C", nullable: false)],
            targetRows: 5000);

        Assert.Equal(DeploymentRisk.Safe, risk.Risk);
        Assert.False(risk.WillBlock);
    }

    [Fact]
    public void Dropped_check_constraint_is_Safe()
    {
        var risk = ConstraintRisk(
            targetRows: 5000,
            srcChecks: null,
            tgtChecks: "chk|CK_Age|([Age]>(0))|disabled=0|notTrusted=0");

        Assert.False(risk.WillBlock);
        Assert.Contains(risk.Findings, f => f.Column == "CK_Age" && f.Risk == DeploymentRisk.Safe);
    }

    [Fact]
    public void Dropped_foreign_key_is_Safe()
    {
        var risk = ConstraintRisk(
            targetRows: 5000,
            srcFks: null,
            tgtFks: "fk|FK_T_P|ref=[dbo].[P]|cols=PId>Id|onDelete=0|onUpdate=0|disabled=0|notTrusted=0");

        Assert.False(risk.WillBlock);
        Assert.Contains(risk.Findings, f => f.Column == "FK_T_P" && f.Risk == DeploymentRisk.Safe);
    }

    [Fact]
    public void Removed_non_empty_schema_will_block()
    {
        // Silinen şema içinde nesne varsa DROP SCHEMA deployment'ta durur → risk üret.
        var source = Database("dev", []);
        var target = Database("prod",
        [
            Obj(Schema("arch"), 1),
            Obj(new ObjectKey("arch", "T", ObjectKind.Table), 2, columns: [Column("Id")], rowCount: 0),
        ]);
        var risks = DeploymentRiskAnalyzer.Analyze(SchemaComparer.Compare(source, target));

        var schemaRisk = Assert.Single(risks, r => r.Table.Kind == ObjectKind.Schema);
        Assert.True(schemaRisk.WillBlock);
        Assert.Contains(schemaRisk.Findings, f => f.Description.Contains("DROP SCHEMA", StringComparison.Ordinal));
    }

    [Fact]
    public void Removed_empty_schema_has_no_risk()
    {
        var source = Database("dev", []);
        var target = Database("prod", [Obj(Schema("arch"), 1)]);
        var risks = DeploymentRiskAnalyzer.Analyze(SchemaComparer.Compare(source, target));

        Assert.DoesNotContain(risks, r => r.Table.Kind == ObjectKind.Schema);
    }
}
