using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

/// <summary>
/// Diğer testlerde doğrudan kapsanmayan karşılaştırma seçeneklerinin davranışı:
/// seçenek KAPALI → fark görünür, AÇIK → yok sayılır.
/// </summary>
public class OptionCoverageGapTests
{
    private const int TId = 100;

    private static ColumnRow Col(int cid, string name, string? collation = null,
        string? defName = null, string? defDef = null, bool defSysNamed = false) =>
        new(TId, cid, name, "sys", "int", 4, 10, 0, true, collation, false, false,
            defName, defDef, defName is null ? (bool?)null : defSysNamed,
            null, null, null, null);

    private static ObjectRow Tbl() => new(TId, "dbo", "T", "U", DateTime.UnixEpoch, 0);

    private static CatalogSet Table(params ColumnRow[] cols) =>
        new() { DatabaseName = "t", ServerName = "S", Objects = [Tbl()], Columns = [.. cols] };

    private static bool Differs(CatalogSet src, CatalogSet tgt, SnapshotOptions o) =>
        SchemaComparer.Compare(
            SnapshotBuilder.Build(src, new ExtractionReport(), o),
            SnapshotBuilder.Build(tgt, new ExtractionReport(), o)).Differences.Count > 0;

    [Fact]
    public void IgnoreColumnOrder_toggles_difference()
    {
        var src = Table(Col(1, "Id"), Col(2, "A"));
        var tgt = Table(Col(1, "A"), Col(2, "Id"));   // aynı kolonlar, ters sıra
        Assert.True(Differs(src, tgt, SnapshotOptions.Default), "KAPALI: sıra farkı görünmeli");
        Assert.False(Differs(src, tgt, SnapshotOptions.Default with { IgnoreColumnOrder = true }), "AÇIK: yok sayılmalı");
    }

    [Fact]
    public void IgnoreCollation_toggles_difference()
    {
        var src = Table(Col(1, "Id"), Col(2, "Name", collation: "Latin1_General_CI_AS"));
        var tgt = Table(Col(1, "Id"), Col(2, "Name", collation: "Turkish_CI_AS"));
        Assert.True(Differs(src, tgt, SnapshotOptions.Default), "KAPALI: collation farkı görünmeli");
        Assert.False(Differs(src, tgt, SnapshotOptions.Default with { IgnoreCollation = true }), "AÇIK: yok sayılmalı");
    }

    [Fact]
    public void IgnoreSystemNamedConstraints_toggles_difference()
    {
        // Sistem adlı DEFAULT, ad farklı ama tanım aynı.
        var src = Table(Col(1, "Id"), Col(2, "A", defName: "DF__T__A__1234", defDef: "((0))", defSysNamed: true));
        var tgt = Table(Col(1, "Id"), Col(2, "A", defName: "DF__T__A__5678", defDef: "((0))", defSysNamed: true));
        Assert.True(Differs(src, tgt, SnapshotOptions.Default with { IgnoreSystemNamedConstraints = false }), "KAPALI: ad farkı görünmeli");
        Assert.False(Differs(src, tgt, SnapshotOptions.Default with { IgnoreSystemNamedConstraints = true }), "AÇIK: ad yok sayılmalı");
    }
}
