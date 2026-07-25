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
}
