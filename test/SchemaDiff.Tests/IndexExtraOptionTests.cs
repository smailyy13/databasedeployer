using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Index'in sürüme bağlı ek seçenekleri: <c>OPTIMIZE_FOR_SEQUENTIAL_KEY</c> (SQL 2019+) ve
/// <c>STATISTICS_NORECOMPUTE</c>.
///
/// Zorunlu index sorgusuna konmadılar: 2019 öncesi sunucuda o sorgu düşse karşılaştırma
/// TÜMDEN biterdi. Ayrı ve opsiyonel sorgudalar; düşerse yalnız bu iki ayar kapsam dışı kalır
/// ve — kritik olan — "kapalı" sayılmazlar, aksi hâlde karışık sürümlü karşılaştırma
/// sahte fark üretirdi.
/// </summary>
public class IndexExtraOptionTests
{
    private const int CustomerId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        CustomerId, id, name, "sys", "int", 4, 10, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    private static IndexRow Idx(int indexId = 2, string name = "IX_A") =>
        new(CustomerId, indexId, name, "NONCLUSTERED", false, false, false, 0, false, false, null);

    private static IndexColumnRow Key(int indexId = 2, int columnId = 2) =>
        new(CustomerId, indexId, columnId, columnId, 1, false, false);

    private static CatalogSet Table(bool seqKey = false, bool noRecompute = false, bool withExtras = true) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "Name")],
        Indexes = [Idx()],
        IndexColumns = [Key()],
        IndexExtras = withExtras ? [new IndexExtraRow(CustomerId, 2, seqKey, noRecompute)] : [],
    };

    private static CatalogSet Empty() => new() { DatabaseName = "test", ServerName = "TESTSRV" };

    private static DatabaseSnapshot Build(CatalogSet c, ExtractionReport? report = null) =>
        SnapshotBuilder.Build(c, report ?? new ExtractionReport(), WithScripts);

    private static string Script(CatalogSet source, CatalogSet target) =>
        TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(source), Build(target)), null, TableScriptOptions.Default).Sql;

    /// <summary>Sorgunun düşmüş olduğu durumu taklit eden rapor.</summary>
    private static ExtractionReport FailedExtras()
    {
        var report = new ExtractionReport();
        report.FailedQueries.Add("indexExtras");
        return report;
    }

    // --- karşılaştırma ---

    [Fact]
    public void Sequential_key_difference_is_detected()
    {
        Assert.Single(SchemaComparer.Compare(Build(Table(seqKey: true)), Build(Table())).Differences);
    }

    [Fact]
    public void Statistics_norecompute_difference_is_detected()
    {
        Assert.Single(SchemaComparer.Compare(Build(Table(noRecompute: true)), Build(Table())).Differences);
    }

    [Fact]
    public void Default_options_do_not_change_the_canonical_text()
    {
        // İkisi de varsayılan KAPALI: mevcut şemaların hash'i değişmemeli.
        var canonical = Build(Table()).Objects[TestFactory.Table("Customer")].PartCanonical["indexes"];

        Assert.DoesNotContain("seqKey", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("statsNoRecompute", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Ignore_index_physical_options_suppresses_them()
    {
        var opts = SnapshotOptions.Default with { KeepDisplayScripts = true, IgnoreIndexPhysicalOptions = true };
        var on = SnapshotBuilder.Build(Table(seqKey: true, noRecompute: true), new ExtractionReport(), opts);
        var off = SnapshotBuilder.Build(Table(), new ExtractionReport(), opts);

        Assert.Empty(SchemaComparer.Compare(on, off).Differences);
    }

    // --- script ---

    [Fact]
    public void Sequential_key_is_written_when_on()
    {
        Assert.Contains("WITH (OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);", Script(Table(seqKey: true), Empty()));
    }

    [Fact]
    public void Statistics_norecompute_is_written_when_on()
    {
        Assert.Contains("WITH (STATISTICS_NORECOMPUTE = ON);", Script(Table(noRecompute: true), Empty()));
    }

    [Fact]
    public void Both_options_share_one_with_clause()
    {
        Assert.Contains(
            "WITH (STATISTICS_NORECOMPUTE = ON, OPTIMIZE_FOR_SEQUENTIAL_KEY = ON);",
            Script(Table(seqKey: true, noRecompute: true), Empty()));
    }

    [Fact]
    public void Nothing_is_written_when_both_are_default()
    {
        var script = Script(Table(), Empty());

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_A]", script);
        Assert.DoesNotContain("OPTIMIZE_FOR_SEQUENTIAL_KEY", script);
        Assert.DoesNotContain("STATISTICS_NORECOMPUTE", script);
    }

    [Fact]
    public void Option_change_recreates_the_index()
    {
        var script = Script(Table(seqKey: true), Table());

        Assert.Contains("DROP INDEX [IX_A] ON [dbo].[Customer];", script);
        Assert.Contains("OPTIMIZE_FOR_SEQUENTIAL_KEY = ON", script);
    }

    [Fact]
    public void New_table_index_carries_the_options()
    {
        var snapshot = Build(Table(seqKey: true, noRecompute: true)).Objects[TestFactory.Table("Customer")];

        Assert.Contains("STATISTICS_NORECOMPUTE = ON", snapshot.DisplayScript!);
        Assert.Contains("OPTIMIZE_FOR_SEQUENTIAL_KEY = ON", snapshot.DisplayScript!);
    }

    // --- sorgu düştüğünde ---

    [Fact]
    public void Unreadable_options_are_not_treated_as_off()
    {
        // 2019 kaynak ↔ 2016 hedef: eski tarafta sorgu düşer. "Kapalı" sayarsak her index
        // için sahte fark çıkar ve gereksiz DROP+CREATE üretilir.
        var modern = Build(Table(seqKey: true));
        var legacy = SnapshotBuilder.Build(Table(seqKey: true), FailedExtras(), WithScripts);

        var canonical = legacy.Objects[TestFactory.Table("Customer")].PartCanonical["indexes"];
        Assert.DoesNotContain("seqKey", canonical, StringComparison.Ordinal);
        Assert.Contains("seqKey", modern.Objects[TestFactory.Table("Customer")].PartCanonical["indexes"], StringComparison.Ordinal);
    }

    [Fact]
    public void Unreadable_options_are_reported_as_a_warning()
    {
        var report = FailedExtras();
        SnapshotBuilder.Build(Table(seqKey: true), report, WithScripts);

        Assert.Contains(report.Warnings, w => w.Contains("OPTIMIZE_FOR_SEQUENTIAL_KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void Unreadable_options_produce_no_script_noise()
    {
        var legacySource = SnapshotBuilder.Build(Table(seqKey: true), FailedExtras(), WithScripts);
        var legacyTarget = SnapshotBuilder.Build(Table(seqKey: true), FailedExtras(), WithScripts);

        Assert.Empty(SchemaComparer.Compare(legacySource, legacyTarget).Differences);
    }
}
