using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Full-text katalog (veritabanı seviyesi obje) ve full-text index (tablonun parçası —
/// tablo başına en fazla bir tane olduğu için ayrı obje değil).
///
/// Sıra kritik: katalog → tablo + KEY INDEX → full-text index. Index, KEY INDEX'e ve
/// kataloğa bağlıdır; ikisinden biri yoksa CREATE patlar.
/// </summary>
public class FullTextTests
{
    private const int CustomerId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        CustomerId, id, name, "sys", "nvarchar", 200, 0, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    private static FullTextIndexRow Index(
        string keyIndex = "UX_Customer_Id", string catalog = "FTC_Main", bool enabled = true,
        string changeTracking = "AUTO", int? stoplistId = 0, string? stoplistName = null) =>
        new(CustomerId, keyIndex, catalog, enabled, changeTracking, stoplistId, stoplistName);

    private static FullTextIndexColumnRow FtCol(int columnId, int typeColumnId = 0, int languageId = 1033) =>
        new(CustomerId, columnId, typeColumnId, languageId);

    private static CatalogSet Table(
        List<FullTextIndexRow>? indexes = null,
        List<FullTextIndexColumnRow>? indexColumns = null,
        List<FullTextCatalogRow>? catalogs = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "Notes"), Col(3, "Extension")],
        FullTextCatalogs = catalogs ?? [],
        FullTextIndexes = indexes ?? [],
        FullTextIndexColumns = indexColumns ?? [],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(CatalogSet source, CatalogSet target) =>
        TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(source), Build(target)), null, TableScriptOptions.Default).Sql;

    private static CatalogSet Indexed(
        bool enabled = true, string changeTracking = "AUTO", int? stoplistId = 0, string? stoplistName = null) =>
        Table([Index(enabled: enabled, changeTracking: changeTracking,
                     stoplistId: stoplistId, stoplistName: stoplistName)],
              [FtCol(2)]);

    // --- katalog ---

    [Fact]
    public void Catalog_is_a_database_level_object_without_schema()
    {
        var snapshot = Build(Table(catalogs: [new FullTextCatalogRow(5, "FTC_Main", true, false)]));
        var key = new ObjectKey(string.Empty, "FTC_Main", ObjectKind.FullTextCatalog);

        Assert.True(snapshot.Objects.ContainsKey(key));
        Assert.Equal("FULLTEXT CATALOG [FTC_Main]", key.ToString());
    }

    [Fact]
    public void Catalog_script_carries_accent_sensitivity()
    {
        var snapshot = Build(Table(catalogs: [new FullTextCatalogRow(5, "FTC_Main", false, false)]));
        var script = snapshot.Objects[new ObjectKey(string.Empty, "FTC_Main", ObjectKind.FullTextCatalog)].DisplayScript;

        Assert.Contains("CREATE FULLTEXT CATALOG [FTC_Main]", script!);
        Assert.Contains("ACCENT_SENSITIVITY = OFF", script!);
    }

    [Fact]
    public void Default_catalog_is_marked_as_default()
    {
        var snapshot = Build(Table(catalogs: [new FullTextCatalogRow(5, "FTC_Main", true, true)]));
        var script = snapshot.Objects[new ObjectKey(string.Empty, "FTC_Main", ObjectKind.FullTextCatalog)].DisplayScript;

        Assert.Contains("AS DEFAULT", script!);
    }

    [Fact]
    public void Accent_sensitivity_difference_is_detected()
    {
        var sensitive = Build(Table(catalogs: [new FullTextCatalogRow(5, "FTC_Main", true, false)]));
        var insensitive = Build(Table(catalogs: [new FullTextCatalogRow(5, "FTC_Main", false, false)]));

        var diff = Assert.Single(SchemaComparer.Compare(sensitive, insensitive).Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
    }

    [Fact]
    public void New_catalog_is_created_before_tables()
    {
        // Tabloların full-text index'i kataloğa bağlı: katalog 1. bölümde, en önce.
        var result = SchemaComparer.Compare(
            Build(Table(catalogs: [new FullTextCatalogRow(5, "FTC_Main", true, false)])), Build(Table()));
        var script = TypeScriptGenerator.Generate(result, null, TypeScriptOptions.Default);

        Assert.Contains("CREATE FULLTEXT CATALOG [FTC_Main]", script.Sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'FTC_Main')", script.Sql);
    }

    [Fact]
    public void Removed_catalog_is_dropped_when_drops_are_enabled()
    {
        var result = SchemaComparer.Compare(
            Build(Table()), Build(Table(catalogs: [new FullTextCatalogRow(5, "FTC_Old", true, false)])));
        var script = TypeScriptGenerator.Generate(result, null, new TypeScriptOptions { IncludeDrops = true });

        Assert.Contains("DROP FULLTEXT CATALOG [FTC_Old];", script.Sql);
    }

    // --- index: karşılaştırma ---

    [Fact]
    public void Identical_full_text_indexes_produce_no_difference()
    {
        Assert.Empty(SchemaComparer.Compare(Build(Indexed()), Build(Indexed())).Differences);
    }

    [Fact]
    public void Missing_full_text_index_is_reported_on_its_own_part()
    {
        var diff = Assert.Single(SchemaComparer.Compare(Build(Indexed()), Build(Table())).Differences);

        Assert.Contains("fullText", diff.ChangedParts);
    }

    [Fact]
    public void Change_tracking_difference_is_detected()
    {
        Assert.Single(SchemaComparer.Compare(
            Build(Indexed(changeTracking: "MANUAL")), Build(Indexed())).Differences);
    }

    [Fact]
    public void Stoplist_difference_is_detected()
    {
        Assert.Single(SchemaComparer.Compare(
            Build(Indexed(stoplistId: null)), Build(Indexed())).Differences);
    }

    [Fact]
    public void Index_without_key_index_is_ignored_rather_than_half_compared()
    {
        // KEY INDEX okunamadıysa (yetki/eksik satır) yarım kanonik yazmak sahte fark üretir.
        var broken = Build(Table([Index(keyIndex: null!)], [FtCol(2)]));

        Assert.False(broken.Objects[TestFactory.Table("Customer")].Parts.ContainsKey("fullText"));
    }

    // --- index: script ---

    [Fact]
    public void Added_full_text_index_is_created_with_key_index_and_catalog()
    {
        var script = Script(Indexed(), Table());

        Assert.Contains(
            "CREATE FULLTEXT INDEX ON [dbo].[Customer] ([Notes] LANGUAGE 1033) " +
            "KEY INDEX [UX_Customer_Id] ON [FTC_Main] WITH (CHANGE_TRACKING = AUTO, STOPLIST = SYSTEM);",
            script);
    }

    [Fact]
    public void Removed_full_text_index_is_dropped()
    {
        var script = Script(Table(), Indexed());

        Assert.Contains("DROP FULLTEXT INDEX ON [dbo].[Customer];", script);
        Assert.DoesNotContain("CREATE FULLTEXT INDEX", script);
    }

    [Fact]
    public void Changed_full_text_index_is_dropped_and_recreated()
    {
        // Tablo başına tek index var: "değişti" hâli yok, baştan kurulur.
        var source = Table([Index(changeTracking: "MANUAL")], [FtCol(2)]);
        var target = Table([Index()], [FtCol(2)]);

        var script = Script(source, target);
        var drop = script.IndexOf("DROP FULLTEXT INDEX", StringComparison.Ordinal);
        var create = script.IndexOf("CREATE FULLTEXT INDEX", StringComparison.Ordinal);

        Assert.True(drop >= 0 && create > drop, "önce DROP, sonra CREATE");
        Assert.Contains("CHANGE_TRACKING = MANUAL", script);
    }

    [Fact]
    public void Type_column_is_written()
    {
        var source = Table([Index()], [FtCol(2, typeColumnId: 3)]);
        var script = Script(source, Table());

        Assert.Contains("[Notes] TYPE COLUMN [Extension] LANGUAGE 1033", script);
    }

    [Fact]
    public void Default_language_is_not_written()
    {
        // LCID 0 "sunucu varsayılanı" demek; yazmak ortama bağımlılık yaratır.
        var source = Table([Index()], [FtCol(2, languageId: 0)]);
        var script = Script(source, Table());

        Assert.Contains("([Notes])", script);
        Assert.DoesNotContain("LANGUAGE", script);
    }

    [Fact]
    public void User_stoplist_is_bracketed_but_keywords_are_not()
    {
        var userList = Script(Table([Index(stoplistId: 7, stoplistName: "MyStoplist")], [FtCol(2)]), Table());
        Assert.Contains("STOPLIST = [MyStoplist]", userList);

        var off = Script(Table([Index(stoplistId: null)], [FtCol(2)]), Table());
        Assert.Contains("STOPLIST = OFF", off);
    }

    [Fact]
    public void Disabled_index_is_created_then_disabled()
    {
        // CREATE FULLTEXT INDEX her zaman AKTİF doğar.
        var script = Script(Indexed(enabled: false), Table());
        var create = script.IndexOf("CREATE FULLTEXT INDEX", StringComparison.Ordinal);
        var disable = script.IndexOf("ALTER FULLTEXT INDEX ON [dbo].[Customer] DISABLE;", StringComparison.Ordinal);

        Assert.True(create >= 0 && disable > create, "önce kurulmalı, sonra kapatılmalı");
    }

    // --- yeni tablo ---

    [Fact]
    public void New_table_script_contains_its_full_text_index()
    {
        var snapshot = Build(Indexed()).Objects[TestFactory.Table("Customer")];

        Assert.Contains("CREATE FULLTEXT INDEX ON [dbo].[Customer]", snapshot.DisplayScript!);
    }

    [Fact]
    public void New_table_full_text_index_comes_after_the_table_in_its_own_batch()
    {
        var snapshot = Build(Indexed()).Objects[TestFactory.Table("Customer")];
        var table = snapshot.DisplayScript!.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        var go = snapshot.DisplayScript!.IndexOf("GO", StringComparison.Ordinal);
        var ft = snapshot.DisplayScript!.IndexOf("CREATE FULLTEXT INDEX", StringComparison.Ordinal);

        Assert.True(table < go && go < ft, "sıra: CREATE TABLE → GO → CREATE FULLTEXT INDEX");
    }

    // --- arayüz ve kapsam ---

    [Fact]
    public void Change_catalog_lists_full_text_under_its_own_folder()
    {
        var result = SchemaComparer.Compare(Build(Indexed()), Build(Table()));
        var change = Assert.Single(ChangeCatalog.Build(result));

        Assert.Contains(change.Children, c => c.Category == "Full-Text");
    }

    [Fact]
    public void Coverage_probe_marks_full_text_as_covered()
    {
        foreach (var item in new[] { "Full-text katalogları", "Full-text index'ler" })
            Assert.True(Assert.Single(CoverageProbe.Probes, p => p.Item == item).Covered, item);
    }
}
