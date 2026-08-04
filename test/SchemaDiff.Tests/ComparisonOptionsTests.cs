using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

/// <summary>
/// Yeni "Ignore X" karşılaştırma seçeneklerinin gerçekten iş yaptığını doğrular:
/// seçenek AÇIKken iki nesne EŞİT, KAPALIyken FARKLI (Changed) sayılmalı. Snapshot'ı
/// gerçek katalog satırlarından SnapshotBuilder ile kurar — DB gerekmez.
/// </summary>
public class ComparisonOptionsTests
{
    private const int TableId = 100, ProcId = 200, TrigId = 300;

    private static DatabaseSnapshot Build(CatalogSet c, SnapshotOptions options) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), options);

    private static bool Equal(CatalogSet source, CatalogSet target, SnapshotOptions options) =>
        SchemaComparer.Compare(Build(source, options), Build(target, options)).Differences.Count == 0;

    // --- tablo + index (fill factor / padding) ---

    private static ColumnRow IdCol() => new(
        TableId, 1, "Id", "sys", "int", 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null);

    private static CatalogSet TableWithIndex(byte fill, bool padded) => new()
    {
        DatabaseName = "t", ServerName = "s",
        Objects = [new ObjectRow(TableId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [IdCol()],
        Indexes = [new IndexRow(TableId, 2, "IX", "NONCLUSTERED", false, false, false, fill, padded, false, null)],
        IndexColumns = [new IndexColumnRow(TableId, 2, 1, 1, 1, false, false)],
    };

    [Fact]
    public void IgnoreFillFactor_makes_fill_difference_equal()
    {
        var a = TableWithIndex(fill: 80, padded: false);
        var b = TableWithIndex(fill: 90, padded: false);

        Assert.False(Equal(a, b, SnapshotOptions.Default));
        Assert.True(Equal(a, b, SnapshotOptions.Default with { IgnoreFillFactor = true }));
    }

    [Fact]
    public void IgnoreIndexPadding_makes_padding_difference_equal()
    {
        var a = TableWithIndex(fill: 0, padded: true);
        var b = TableWithIndex(fill: 0, padded: false);

        Assert.False(Equal(a, b, SnapshotOptions.Default));
        Assert.True(Equal(a, b, SnapshotOptions.Default with { IgnoreIndexPadding = true }));
    }

    [Fact]
    public void IgnoreIndexPhysical_covers_both_fill_and_padding()
    {
        var a = TableWithIndex(fill: 70, padded: true);
        var b = TableWithIndex(fill: 90, padded: false);

        Assert.True(Equal(a, b, SnapshotOptions.Default with { IgnoreIndexPhysicalOptions = true }));
    }

    // --- identity seed / increment ---

    private static CatalogSet TableWithIdentity(string seed, string inc) => new()
    {
        DatabaseName = "t", ServerName = "s",
        Objects = [new ObjectRow(TableId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [new ColumnRow(TableId, 1, "Id", "sys", "int", 4, 10, 0, false, null,
            true, false, null, null, null, null, null, seed, inc)],
    };

    [Fact]
    public void IgnoreIdentitySeed_ignores_seed_but_not_increment()
    {
        var a = TableWithIdentity(seed: "1", inc: "1");
        var b = TableWithIdentity(seed: "1000", inc: "1");

        Assert.False(Equal(a, b, SnapshotOptions.Default));
        Assert.True(Equal(a, b, SnapshotOptions.Default with { IgnoreIdentitySeed = true }));

        // Seed yok sayılsa da increment farkı hâlâ yakalanır.
        var c = TableWithIdentity(seed: "1", inc: "5");
        Assert.False(Equal(a, c, SnapshotOptions.Default with { IgnoreIdentitySeed = true }));
    }

    [Fact]
    public void IgnoreIdentityIncrement_ignores_increment_only()
    {
        var a = TableWithIdentity(seed: "1", inc: "1");
        var b = TableWithIdentity(seed: "1", inc: "10");

        Assert.False(Equal(a, b, SnapshotOptions.Default));
        Assert.True(Equal(a, b, SnapshotOptions.Default with { IgnoreIdentityIncrement = true }));
    }

    // --- modül SET seçenekleri (ANSI NULLS / quoted identifiers) ---

    private static CatalogSet Proc(bool ansiNulls, bool quoted) => new()
    {
        DatabaseName = "t", ServerName = "s",
        Objects = [new ObjectRow(ProcId, "dbo", "P", "P", DateTime.UnixEpoch, 0)],
        Modules = [new ModuleRow(ProcId, "CREATE PROCEDURE dbo.P AS SELECT 1", ansiNulls, quoted)],
    };

    [Fact]
    public void IgnoreAnsiNulls_makes_ansi_nulls_difference_equal()
    {
        var a = Proc(ansiNulls: true, quoted: true);
        var b = Proc(ansiNulls: false, quoted: true);

        Assert.False(Equal(a, b, SnapshotOptions.Default));
        Assert.True(Equal(a, b, SnapshotOptions.Default with { IgnoreAnsiNulls = true }));
    }

    [Fact]
    public void IgnoreQuotedIdentifiers_makes_quoted_difference_equal()
    {
        var a = Proc(ansiNulls: true, quoted: true);
        var b = Proc(ansiNulls: true, quoted: false);

        Assert.False(Equal(a, b, SnapshotOptions.Default));
        Assert.True(Equal(a, b, SnapshotOptions.Default with { IgnoreQuotedIdentifiers = true }));
    }

    // --- DML trigger state ---

    private static CatalogSet Trigger(bool disabled) => new()
    {
        DatabaseName = "t", ServerName = "s",
        Objects =
        [
            new ObjectRow(TableId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
            new ObjectRow(TrigId, "dbo", "TR_Customer", "TR", DateTime.UnixEpoch, TableId),
        ],
        Columns = [IdCol()],
        Modules = [new ModuleRow(TrigId, "CREATE TRIGGER dbo.TR_Customer ON dbo.Customer AFTER INSERT AS SELECT 1", true, true)],
        Triggers = [new TriggerRow(TrigId, disabled, false)],
    };

    [Fact]
    public void IgnoreDmlTriggerState_makes_enabled_disabled_difference_equal()
    {
        var a = Trigger(disabled: false);
        var b = Trigger(disabled: true);

        Assert.False(Equal(a, b, SnapshotOptions.Default));
        Assert.True(Equal(a, b, SnapshotOptions.Default with { IgnoreDmlTriggerState = true }));
    }
}
