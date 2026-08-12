using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Kullanıcı istatistikleri (CREATE STATISTICS). Katalogda üç tür istatistik vardır;
/// yalnızca user_created olanlar şema objesidir — otomatik üretilenler ve index'in
/// taşıdıkları kapsam dışıdır (sorgu onları zaten getirmez, burada test edilen kısım
/// getirilenlerin karşılaştırılması ve script'lenmesidir).
/// </summary>
public class StatisticsTests
{
    private const int CustomerId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        CustomerId, id, name, "sys", "int", 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null);

    private static StatisticRow Stat(
        int statsId, string name, bool noRecompute = false, string? filter = null, bool incremental = false) =>
        new(CustomerId, statsId, name, noRecompute, filter, incremental);

    /// <summary>İstatistiğin kolonu. <paramref name="ordinal"/> tanımdaki sıradır (1'den başlar).</summary>
    private static StatisticColumnRow StatCol(int statsId, int columnId, int ordinal) =>
        new(CustomerId, statsId, ordinal, columnId);

    private static CatalogSet Table(
        List<StatisticRow>? statistics = null,
        List<StatisticColumnRow>? statisticColumns = null,
        List<ColumnRow>? columns = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = columns ?? [Col(1, "Id"), Col(2, "Name"), Col(3, "Email")],
        Statistics = statistics ?? [],
        StatisticColumns = statisticColumns ?? [],
    };

    private static DatabaseSnapshot Build(CatalogSet c, SnapshotOptions? options = null) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), options ?? WithScripts);

    private static TableScriptResult Generate(DatabaseSnapshot source, DatabaseSnapshot target) =>
        TableScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, TableScriptOptions.Default);

    /// <summary>Tek istatistikli standart katalog: ST_Customer_Email → (Email).</summary>
    private static CatalogSet WithEmailStat(bool noRecompute = false, string? filter = null) =>
        Table([Stat(2, "ST_Customer_Email", noRecompute, filter)], [StatCol(2, 3, 1)]);

    // --- karşılaştırma ---

    [Fact]
    public void Identical_statistics_produce_no_difference()
    {
        Assert.Empty(SchemaComparer.Compare(Build(WithEmailStat()), Build(WithEmailStat())).Differences);
    }

    [Fact]
    public void Statistics_are_a_separate_snapshot_part()
    {
        var snapshot = Build(WithEmailStat()).Objects[TestFactory.Table("Customer")];

        Assert.True(snapshot.Parts.ContainsKey("statistics"));
        Assert.Contains("stat|ST_Customer_Email|cols=Email", snapshot.PartCanonical["statistics"]);
    }

    [Fact]
    public void Missing_statistic_is_reported_as_changed_statistics_part()
    {
        var diff = Assert.Single(SchemaComparer.Compare(Build(WithEmailStat()), Build(Table())).Differences);

        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("statistics", diff.ChangedParts);
    }

    [Fact]
    public void Column_order_inside_statistic_is_significant()
    {
        // İlk kolon histogramı taşır: (Name, Email) ile (Email, Name) aynı istatistik değildir.
        var source = Build(Table([Stat(2, "ST_Multi")], [StatCol(2, 2, 1), StatCol(2, 3, 2)]));
        var target = Build(Table([Stat(2, "ST_Multi")], [StatCol(2, 3, 1), StatCol(2, 2, 2)]));

        Assert.Single(SchemaComparer.Compare(source, target).Differences);
    }

    [Fact]
    public void Norecompute_difference_is_detected()
    {
        Assert.Single(SchemaComparer.Compare(
            Build(WithEmailStat(noRecompute: true)), Build(WithEmailStat())).Differences);
    }

    [Fact]
    public void Filter_difference_is_detected()
    {
        Assert.Single(SchemaComparer.Compare(
            Build(WithEmailStat(filter: "([Id]>(0))")), Build(WithEmailStat())).Differences);
    }

    [Fact]
    public void Statistic_order_in_catalog_does_not_affect_the_hash()
    {
        // Katalog satır sırası sunucudan sunucuya değişebilir; kanonik metin sıralı olmalı.
        var a = Build(Table(
            [Stat(2, "ST_A"), Stat(3, "ST_B")], [StatCol(2, 2, 1), StatCol(3, 3, 1)]));
        var b = Build(Table(
            [Stat(3, "ST_B"), Stat(2, "ST_A")], [StatCol(3, 3, 1), StatCol(2, 2, 1)]));

        Assert.Empty(SchemaComparer.Compare(a, b).Differences);
    }

    [Fact]
    public void Ignore_statistics_option_suppresses_the_difference()
    {
        var opts = SnapshotOptions.Default with { KeepDisplayScripts = true, IgnoreStatistics = true };

        Assert.Empty(SchemaComparer.Compare(
            Build(WithEmailStat(), opts), Build(Table(), opts)).Differences);
    }

    [Fact]
    public void Statistics_are_compared_by_default()
    {
        // Varsayılan kapalı olmamalı: hedefte eksik istatistik sorgu planını değiştirir.
        Assert.False(SnapshotOptions.Default.IgnoreStatistics);
    }

    // --- script üretimi: değişen tablo ---

    [Fact]
    public void Added_statistic_is_created()
    {
        var script = Generate(Build(WithEmailStat()), Build(Table()));

        Assert.Contains(
            "CREATE STATISTICS [ST_Customer_Email] ON [dbo].[Customer] ([Email]);", script.Sql);
        Assert.DoesNotContain("DROP STATISTICS", script.Sql);
    }

    [Fact]
    public void Removed_statistic_is_dropped()
    {
        var script = Generate(Build(Table()), Build(WithEmailStat()));

        Assert.Contains("DROP STATISTICS [dbo].[Customer].[ST_Customer_Email];", script.Sql);
        Assert.DoesNotContain("CREATE STATISTICS", script.Sql);
    }

    [Fact]
    public void Changed_statistic_is_dropped_and_recreated()
    {
        // ALTER STATISTICS kolon listesini değiştiremez → drop + recreate.
        var source = Build(Table([Stat(2, "ST_X")], [StatCol(2, 2, 1), StatCol(2, 3, 2)]));
        var target = Build(Table([Stat(2, "ST_X")], [StatCol(2, 2, 1)]));

        var script = Generate(source, target);

        Assert.Contains("DROP STATISTICS [dbo].[Customer].[ST_X];", script.Sql);
        Assert.Contains("CREATE STATISTICS [ST_X] ON [dbo].[Customer] ([Name], [Email]);", script.Sql);
    }

    [Fact]
    public void Statistic_drop_runs_before_create()
    {
        // Drop kolon değişikliklerinden önce (pre), create sonra (post) gelmeli.
        var source = Build(Table([Stat(2, "ST_X")], [StatCol(2, 3, 1)]));
        var target = Build(Table([Stat(2, "ST_X")], [StatCol(2, 2, 1)]));

        var sql = Generate(source, target).Sql;
        var drop = sql.IndexOf("DROP STATISTICS", StringComparison.Ordinal);
        var create = sql.IndexOf("CREATE STATISTICS", StringComparison.Ordinal);

        Assert.True(drop >= 0 && create >= 0 && drop < create, "önce DROP, sonra CREATE gelmeli");
    }

    [Fact]
    public void Statistic_drop_runs_before_the_column_it_covers_is_dropped()
    {
        // Silinecek kolonun üstündeki istatistik DROP COLUMN'u engeller: önce istatistik düşmeli.
        var options = new TableScriptOptions { AllowDataLoss = true };
        var source = Build(Table(columns: [Col(1, "Id"), Col(2, "Name")]));
        var target = Build(Table(
            [Stat(2, "ST_Email")], [StatCol(2, 3, 1)],
            [Col(1, "Id"), Col(2, "Name"), Col(3, "Email")]));

        var sql = TableScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, options).Sql;
        var stat = sql.IndexOf("DROP STATISTICS", StringComparison.Ordinal);
        var column = sql.IndexOf("DROP COLUMN [Email]", StringComparison.Ordinal);

        Assert.True(stat >= 0 && column >= 0 && stat < column, "istatistik, kolondan önce düşmeli");
    }

    [Fact]
    public void Statistic_create_runs_after_the_column_it_covers_is_added()
    {
        var source = Build(Table(
            [Stat(2, "ST_Email")], [StatCol(2, 3, 1)],
            [Col(1, "Id"), Col(2, "Name"), Col(3, "Email")]));
        var target = Build(Table(columns: [Col(1, "Id"), Col(2, "Name")]));

        var sql = Generate(source, target).Sql;
        var column = sql.IndexOf("ADD [Email]", StringComparison.Ordinal);
        var stat = sql.IndexOf("CREATE STATISTICS", StringComparison.Ordinal);

        Assert.True(column >= 0 && stat >= 0 && column < stat, "istatistik, kolon eklendikten sonra kurulmalı");
    }

    [Fact]
    public void Norecompute_is_written_into_the_create()
    {
        var script = Generate(Build(WithEmailStat(noRecompute: true)), Build(Table()));

        Assert.Contains(
            "CREATE STATISTICS [ST_Customer_Email] ON [dbo].[Customer] ([Email]) WITH NORECOMPUTE;", script.Sql);
    }

    [Fact]
    public void Filter_is_written_into_the_create()
    {
        var script = Generate(Build(WithEmailStat(filter: "([Id]>(0))")), Build(Table()));

        Assert.Contains(
            "CREATE STATISTICS [ST_Customer_Email] ON [dbo].[Customer] ([Email]) WHERE ([Id]>(0));", script.Sql);
    }

    [Fact]
    public void Filter_and_norecompute_are_written_in_the_right_order()
    {
        // T-SQL grameri: önce WHERE, sonra WITH.
        var script = Generate(Build(WithEmailStat(noRecompute: true, filter: "([Id]>(0))")), Build(Table()));

        Assert.Contains(
            "CREATE STATISTICS [ST_Customer_Email] ON [dbo].[Customer] ([Email]) " +
            "WHERE ([Id]>(0)) WITH NORECOMPUTE;", script.Sql);
    }

    [Fact]
    public void Incremental_statistic_is_written_with_option()
    {
        var source = Build(Table([Stat(2, "ST_Inc", incremental: true)], [StatCol(2, 3, 1)]));
        var script = Generate(source, Build(Table()));

        Assert.Contains("WITH INCREMENTAL = ON;", script.Sql);
    }

    [Fact]
    public void Norecompute_and_incremental_are_combined_in_one_with_clause()
    {
        var source = Build(Table([Stat(2, "ST_Both", noRecompute: true, incremental: true)], [StatCol(2, 3, 1)]));
        var script = Generate(source, Build(Table()));

        Assert.Contains("WITH NORECOMPUTE, INCREMENTAL = ON;", script.Sql);
    }

    [Fact]
    public void Ignore_statistics_option_keeps_statistics_out_of_the_script()
    {
        var opts = SnapshotOptions.Default with { KeepDisplayScripts = true, IgnoreStatistics = true };
        // Kolon farkı script'i yine üretsin; istatistik satırı içinde OLMAMALI.
        var source = Build(Table([Stat(2, "ST_X")], [StatCol(2, 3, 1)],
            [Col(1, "Id"), Col(2, "Name"), Col(3, "Email")]), opts);
        var target = Build(Table(columns: [Col(1, "Id"), Col(2, "Name")]), opts);

        var sql = TableScriptGenerator.Generate(
            SchemaComparer.Compare(source, target), null, TableScriptOptions.Default).Sql;

        Assert.Contains("ADD [Email]", sql);
        Assert.DoesNotContain("STATISTICS", sql);
    }

    // --- script üretimi: yeni tablo ---

    [Fact]
    public void New_table_script_contains_its_statistics()
    {
        var script = Generate(Build(WithEmailStat()), Build(new CatalogSet { DatabaseName = "test", ServerName = "TESTSRV" }));

        Assert.Contains("CREATE TABLE [dbo].[Customer]", script.Sql);
        Assert.Contains("CREATE STATISTICS [ST_Customer_Email] ON [dbo].[Customer] ([Email]);", script.Sql);
    }

    [Fact]
    public void New_table_statistics_are_emitted_in_their_own_batch()
    {
        // CREATE STATISTICS tablo oluştuktan SONRA çalışmalı → araya GO girmeli.
        var snapshot = Build(WithEmailStat()).Objects[TestFactory.Table("Customer")];

        Assert.NotNull(snapshot.DisplayScript);
        var table = snapshot.DisplayScript!.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        var go = snapshot.DisplayScript!.IndexOf("GO", StringComparison.Ordinal);
        var stat = snapshot.DisplayScript!.IndexOf("CREATE STATISTICS", StringComparison.Ordinal);

        Assert.True(table < go && go < stat, "sıra: CREATE TABLE → GO → CREATE STATISTICS");
    }

    [Fact]
    public void New_table_statistics_are_ordered_by_name()
    {
        // Aynı şema iki kez script'lendiğinde metin birebir aynı çıkmalı (deterministiklik).
        var snapshot = Build(Table(
            [Stat(3, "ST_B"), Stat(2, "ST_A")], [StatCol(3, 3, 1), StatCol(2, 2, 1)]))
            .Objects[TestFactory.Table("Customer")];

        var a = snapshot.DisplayScript!.IndexOf("[ST_A]", StringComparison.Ordinal);
        var b = snapshot.DisplayScript!.IndexOf("[ST_B]", StringComparison.Ordinal);

        Assert.True(a >= 0 && b >= 0 && a < b, "istatistikler ada göre sıralı yazılmalı");
    }

    // --- yapısal tanımlar ---

    [Fact]
    public void Statistics_definitions_are_only_kept_with_display_scripts()
    {
        // Yapısal tanım bellekte yer kaplar; karşılaştırma için gerekmez.
        var withScripts = Build(WithEmailStat()).Objects[TestFactory.Table("Customer")];
        var lean = Build(WithEmailStat(), SnapshotOptions.Default).Objects[TestFactory.Table("Customer")];

        Assert.NotNull(withScripts.StatisticsDefinitions);
        Assert.Single(withScripts.StatisticsDefinitions!);
        Assert.Null(lean.StatisticsDefinitions);
    }

    [Fact]
    public void Statistic_without_columns_is_not_scripted()
    {
        // stats_columns okunamadıysa (yetki/sürüm) kolonsuz CREATE STATISTICS üretmek yerine atla.
        var snapshot = Build(Table([Stat(2, "ST_Broken")])).Objects[TestFactory.Table("Customer")];

        Assert.Empty(snapshot.StatisticsDefinitions!);
        Assert.DoesNotContain("CREATE STATISTICS", snapshot.DisplayScript!);
    }

    // --- sorgu çalıştırılamadığında ---

    /// <summary>İstatistik sorgusu düşmüş gibi davranan rapor (eski sürüm / yetki eksiği).</summary>
    private static ExtractionReport FailedStatisticsReport()
    {
        var report = new ExtractionReport();
        report.FailedQueries.Add("statistics");
        return report;
    }

    [Fact]
    public void Unreadable_statistics_are_not_counted_as_absent()
    {
        // "Okuyamadık" ile "yok" aynı şey değil: boş saymak hedefteki istatistik için
        // sahte DROP üretir. Sınıf tamamen karşılaştırma dışı kalmalı.
        var readable = Build(WithEmailStat());
        var unreadable = SnapshotBuilder.Build(WithEmailStat(), FailedStatisticsReport(), WithScripts);

        Assert.False(unreadable.Objects[TestFactory.Table("Customer")].Parts.ContainsKey("statistics"));
        Assert.True(readable.Objects[TestFactory.Table("Customer")].Parts.ContainsKey("statistics"));
    }

    [Fact]
    public void Unreadable_statistics_produce_no_drop_script()
    {
        var source = SnapshotBuilder.Build(Table(), FailedStatisticsReport(), WithScripts);
        var target = SnapshotBuilder.Build(WithEmailStat(), FailedStatisticsReport(), WithScripts);

        Assert.DoesNotContain("DROP STATISTICS", Generate(source, target).Sql);
    }

    [Fact]
    public void Unreadable_statistics_are_reported_as_a_warning()
    {
        var report = FailedStatisticsReport();
        SnapshotBuilder.Build(WithEmailStat(), report, WithScripts);

        Assert.Contains(report.Warnings, w => w.Contains("istatistik", StringComparison.OrdinalIgnoreCase));
    }

    // --- arayüz ağacı ---

    [Fact]
    public void Change_catalog_lists_statistics_under_their_own_folder()
    {
        var result = SchemaComparer.Compare(Build(WithEmailStat()), Build(Table()));
        var change = Assert.Single(ChangeCatalog.Build(result));
        var child = Assert.Single(change.Children, c => c.Category == "Statistics");

        Assert.Equal(ChangeAction.Add, child.Action);
        Assert.Equal("ST_Customer_Email", child.Name);
    }

    [Fact]
    public void Change_catalog_lists_statistics_of_a_dropped_table()
    {
        var empty = Build(new CatalogSet { DatabaseName = "test", ServerName = "TESTSRV" });
        var result = SchemaComparer.Compare(empty, Build(WithEmailStat()));
        var change = Assert.Single(ChangeCatalog.Build(result));

        Assert.Contains(change.Children, c => c.Category == "Statistics" && c.Action == ChangeAction.Delete);
    }

    // --- kapsam sondası ---

    [Fact]
    public void Coverage_probe_marks_user_statistics_as_covered()
    {
        var probe = Assert.Single(CoverageProbe.Probes, p => p.Item == "Kullanıcı istatistikleri");
        Assert.True(probe.Covered);
    }

    [Fact]
    public void Coverage_probe_still_reports_auto_created_statistics_as_out_of_scope()
    {
        var probe = Assert.Single(CoverageProbe.Probes, p => p.Item.StartsWith("Otomatik istatistikler", StringComparison.Ordinal));
        Assert.False(probe.Covered);
    }
}
