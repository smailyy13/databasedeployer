using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Full-text stoplist'ler. Full-text index'ler bunlara ADIYLA başvurur (Dalga 8):
/// stoplist hedefte yoksa index'in CREATE'i patlar.
///
/// Tip üretecindeki TEK istisna: SQL Server kelime seviyesinde ADD/DROP verdiği için
/// değişim tam ve güvenli üretilebiliyor — öteki türlerde olduğu gibi "elle drop+recreate"
/// demeye gerek yok.
/// </summary>
public class FullTextStoplistTests
{
    private const int StoplistId = 5;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };
    private static readonly ObjectKey Key = new(string.Empty, "MyStoplist", ObjectKind.FullTextStoplist);

    private static CatalogSet Catalog(params (string Word, int Language)[] words) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        FullTextStoplists = [new FullTextStoplistRow(StoplistId, "MyStoplist")],
        FullTextStopwords = [.. words.Select(w => new FullTextStopwordRow(StoplistId, w.Word, w.Language))],
    };

    private static CatalogSet Empty() => new() { DatabaseName = "test", ServerName = "TESTSRV" };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static TypeScriptResult Script(CatalogSet source, CatalogSet target, TypeScriptOptions? options = null) =>
        TypeScriptGenerator.Generate(
            SchemaComparer.Compare(Build(source), Build(target)), null, options ?? TypeScriptOptions.Default);

    // --- karşılaştırma ---

    [Fact]
    public void Stoplist_is_a_database_level_object_without_schema()
    {
        Assert.True(Build(Catalog()).Objects.ContainsKey(Key));
        Assert.Equal("FULLTEXT STOPLIST [MyStoplist]", Key.ToString());
    }

    [Fact]
    public void Empty_stoplist_is_still_an_object()
    {
        // "Boş stoplist" ile "stoplist yok" karışmamalı: parça her hâlde yazılır.
        var snapshot = Build(Catalog()).Objects[Key];

        Assert.True(snapshot.Parts.ContainsKey("definition"));
    }

    [Fact]
    public void Missing_stoplist_is_reported_as_added()
    {
        var diff = Assert.Single(SchemaComparer.Compare(Build(Catalog(("the", 1033))), Build(Empty())).Differences);

        Assert.Equal(DiffKind.Added, diff.Kind);
    }

    [Fact]
    public void Identical_word_lists_produce_no_difference()
    {
        Assert.Empty(SchemaComparer.Compare(
            Build(Catalog(("the", 1033), ("and", 1033))),
            Build(Catalog(("the", 1033), ("and", 1033)))).Differences);
    }

    [Fact]
    public void Word_order_in_the_catalog_does_not_matter()
    {
        var a = Build(Catalog(("the", 1033), ("and", 1033)));
        var b = Build(Catalog(("and", 1033), ("the", 1033)));

        Assert.Empty(SchemaComparer.Compare(a, b).Differences);
    }

    [Fact]
    public void Same_word_in_a_different_language_is_a_different_entry()
    {
        // Kelime + dil birlikte kimliktir.
        Assert.Single(SchemaComparer.Compare(
            Build(Catalog(("ve", 1055))), Build(Catalog(("ve", 1033)))).Differences);
    }

    // --- yeni / silinen ---

    [Fact]
    public void New_stoplist_is_created_then_filled_word_by_word()
    {
        var script = Script(Catalog(("the", 1033)), Empty()).Sql;

        Assert.Contains("CREATE FULLTEXT STOPLIST [MyStoplist];", script);
        Assert.Contains("ALTER FULLTEXT STOPLIST [MyStoplist] ADD N'the' LANGUAGE 1033;", script);
    }

    [Fact]
    public void Create_does_not_copy_the_system_stoplist()
    {
        // FROM SYSTEM STOPLIST sunucu sürümüne göre farklı içerik üretir.
        Assert.DoesNotContain("FROM SYSTEM STOPLIST", Script(Catalog(("the", 1033)), Empty()).Sql);
    }

    [Fact]
    public void Removed_stoplist_is_dropped_when_drops_are_enabled()
    {
        var script = Script(Empty(), Catalog(("the", 1033)), new TypeScriptOptions { IncludeDrops = true });

        Assert.Contains("DROP FULLTEXT STOPLIST [MyStoplist];", script.Sql);
    }

    [Fact]
    public void Quotes_in_a_stopword_are_escaped()
    {
        var script = Script(Catalog(("it's", 1033)), Empty()).Sql;

        Assert.Contains("ADD N'it''s' LANGUAGE 1033;", script);
    }

    // --- kelime seviyesi değişim ---

    [Fact]
    public void Added_word_is_scripted_as_alter_add()
    {
        var script = Script(Catalog(("the", 1033), ("and", 1033)), Catalog(("the", 1033))).Sql;

        Assert.Contains("ALTER FULLTEXT STOPLIST [MyStoplist] ADD N'and' LANGUAGE 1033;", script);
    }

    [Fact]
    public void Removed_word_is_scripted_as_alter_drop()
    {
        var script = Script(Catalog(("the", 1033)), Catalog(("the", 1033), ("and", 1033))).Sql;

        Assert.Contains("ALTER FULLTEXT STOPLIST [MyStoplist] DROP N'and' LANGUAGE 1033;", script);
    }

    [Fact]
    public void Word_change_does_not_drop_and_recreate_the_stoplist()
    {
        // Öteki türlerden farkı bu: değişim tam üretilebildiği için "elle yap" demiyoruz.
        var script = Script(Catalog(("the", 1033)), Catalog(("and", 1033)));

        Assert.DoesNotContain("DROP FULLTEXT STOPLIST", script.Sql);
        Assert.DoesNotContain("CREATE FULLTEXT STOPLIST", script.Sql);
        Assert.Contains("ADD N'the'", script.Sql);
        Assert.Contains("DROP N'and'", script.Sql);
    }

    [Fact]
    public void Changed_stoplist_is_counted_as_included_not_skipped()
    {
        var script = Script(Catalog(("the", 1033)), Catalog(("and", 1033)));

        Assert.Contains(Key, script.Included);
        Assert.DoesNotContain(script.Skipped, s => s.Key == Key);
    }

    // --- sıra ve kapsam ---

    [Fact]
    public void Stoplist_is_created_before_the_full_text_catalog()
    {
        // Full-text index hem stoplist'e hem kataloğa bağlı; ikisi de tablolardan önce.
        var source = new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            FullTextStoplists = [new FullTextStoplistRow(StoplistId, "MyStoplist")],
            FullTextCatalogs = [new FullTextCatalogRow(9, "FTC_Main", true, false)],
        };

        var script = Script(source, Empty()).Sql;
        var stoplist = script.IndexOf("CREATE FULLTEXT STOPLIST", StringComparison.Ordinal);
        var catalog = script.IndexOf("CREATE FULLTEXT CATALOG", StringComparison.Ordinal);

        Assert.True(stoplist >= 0 && catalog > stoplist, "stoplist katalogdan önce kurulmalı");
    }

    [Fact]
    public void Coverage_probe_marks_stoplists_as_covered()
    {
        var probe = Assert.Single(CoverageProbe.Probes, p => p.Item == "Full-text stoplist'ler");
        Assert.True(probe.Covered);
    }

    [Fact]
    public void Change_catalog_names_the_object_type()
    {
        var result = SchemaComparer.Compare(Build(Catalog(("the", 1033))), Build(Empty()));
        var change = Assert.Single(ChangeCatalog.Build(result));

        Assert.Equal("Full-Text Stoplist", change.ObjectType);
        Assert.Equal(ObjectKind.FullTextStoplist, ObjectKindLabels.Resolve(change.ObjectType));
    }
}
