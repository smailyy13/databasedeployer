using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// XML schema collection'lar. Tipli XML kolonları (Dalga 6) bunlara ADIYLA başvurur:
/// koleksiyon hedefte yoksa tablonun CREATE'i patlar. Bu yüzden içeriği (XSD) hiç
/// okunamasa bile "var mı / namespace'leri aynı mı" tespiti tek başına değerlidir.
///
/// İçerik ancak <c>XML_SCHEMA_NAMESPACE</c> ile okunabilir; o sorgu düşerse karşılaştırma
/// ad + namespace üzerinden sürer, yalnız CREATE script'i üretilemez.
/// </summary>
public class XmlSchemaCollectionTests
{
    private const int CollectionId = 65536;
    private const string Xsd =
        "<xsd:schema xmlns:xsd=\"http://www.w3.org/2001/XMLSchema\"><xsd:element name=\"Order\" /></xsd:schema>";

    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };
    private static readonly ObjectKey Key = new("dbo", "OrderSchema", ObjectKind.XmlSchemaCollection);

    private static CatalogSet Catalog(
        string[]? namespaces = null, string? content = Xsd, string schema = "dbo", string name = "OrderSchema") => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        XmlSchemaCollections = [new XmlSchemaCollectionRow(CollectionId, schema, name)],
        XmlSchemaNamespaces = [.. (namespaces ?? ["urn:orders"]).Select(n => new XmlSchemaNamespaceRow(CollectionId, n))],
        XmlSchemaContents = content is null ? [] : [new XmlSchemaContentRow(CollectionId, content)],
    };

    private static CatalogSet Empty() => new() { DatabaseName = "test", ServerName = "TESTSRV" };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    // --- obje olarak görünürlük ---

    [Fact]
    public void Collection_is_a_schema_qualified_object()
    {
        Assert.True(Build(Catalog()).Objects.ContainsKey(Key));
    }

    [Fact]
    public void Built_in_sys_collection_is_not_a_user_object()
    {
        var snapshot = Build(Catalog(schema: "sys", name: "sys"));

        Assert.DoesNotContain(snapshot.Objects.Keys, k => k.Kind == ObjectKind.XmlSchemaCollection);
    }

    [Fact]
    public void Missing_collection_is_reported_as_added()
    {
        var diff = Assert.Single(SchemaComparer.Compare(Build(Catalog()), Build(Empty())).Differences);

        Assert.Equal(DiffKind.Added, diff.Kind);
        Assert.Equal(ObjectKind.XmlSchemaCollection, diff.Key.Kind);
    }

    [Fact]
    public void Identical_collections_produce_no_difference()
    {
        Assert.Empty(SchemaComparer.Compare(Build(Catalog()), Build(Catalog())).Differences);
    }

    [Fact]
    public void Namespace_difference_is_detected()
    {
        Assert.Single(SchemaComparer.Compare(
            Build(Catalog(["urn:orders", "urn:extra"])), Build(Catalog())).Differences);
    }

    [Fact]
    public void Namespace_order_does_not_matter()
    {
        // Katalog satır sırası sunucudan sunucuya değişir; kanonik sıralı olmalı.
        var a = Build(Catalog(["urn:a", "urn:b"]));
        var b = Build(Catalog(["urn:b", "urn:a"]));

        Assert.Empty(SchemaComparer.Compare(a, b).Differences);
    }

    [Fact]
    public void Content_difference_is_detected_even_when_namespaces_match()
    {
        // Asıl değer burada: namespace listesi aynı kalıp içeriği değişen koleksiyon.
        var changed = Catalog(content: Xsd.Replace("Order", "Invoice"));

        var diff = Assert.Single(SchemaComparer.Compare(Build(changed), Build(Catalog())).Differences);
        Assert.Contains("content", diff.ChangedParts);
    }

    // --- script ---

    [Fact]
    public void New_collection_is_created_with_its_schema_text()
    {
        var result = SchemaComparer.Compare(Build(Catalog()), Build(Empty()));
        var script = TypeScriptGenerator.Generate(result, null, TypeScriptOptions.Default);

        Assert.Contains("CREATE XML SCHEMA COLLECTION [dbo].[OrderSchema] AS N'", script.Sql);
        Assert.Contains("xsd:element", script.Sql);
    }

    [Fact]
    public void Single_quotes_in_the_schema_are_escaped()
    {
        // XSD içinde tek tırnak varsa kaçırılmazsa string literal kapanır ve SQL bozulur.
        var quoted = Catalog(content: "<xsd:schema fixed='a' />");
        var result = SchemaComparer.Compare(Build(quoted), Build(Empty()));
        var script = TypeScriptGenerator.Generate(result, null, TypeScriptOptions.Default);

        Assert.Contains("fixed=''a''", script.Sql);
    }

    [Fact]
    public void Collection_is_created_before_tables_that_use_it()
    {
        // Tipli XML kolonu koleksiyona bağlı: 1. bölümde ve en önce kurulmalı.
        Assert.Contains(ObjectKind.XmlSchemaCollection, TypeScriptGenerator.HandledKinds);
    }

    [Fact]
    public void Removed_collection_is_dropped_when_drops_are_enabled()
    {
        var result = SchemaComparer.Compare(Build(Empty()), Build(Catalog()));
        var script = TypeScriptGenerator.Generate(result, null, new TypeScriptOptions { IncludeDrops = true });

        Assert.Contains("DROP XML SCHEMA COLLECTION [dbo].[OrderSchema];", script.Sql);
    }

    [Fact]
    public void Changed_collection_is_skipped_with_a_reason()
    {
        // ALTER XML SCHEMA COLLECTION yalnız EKLEYEBİLİR; çıkarma yoktur, otomatik üretmiyoruz.
        var result = SchemaComparer.Compare(Build(Catalog(["urn:new"])), Build(Catalog()));
        var script = TypeScriptGenerator.Generate(result, null, TypeScriptOptions.Default);

        Assert.Contains(script.Skipped, s => s.Key.Kind == ObjectKind.XmlSchemaCollection);
    }

    // --- içerik okunamadığında ---

    [Fact]
    public void Collection_without_readable_content_is_still_compared()
    {
        // XML_SCHEMA_NAMESPACE düşse bile ad + namespace karşılaştırması sürmeli.
        var diff = Assert.Single(SchemaComparer.Compare(
            Build(Catalog(content: null)), Build(Empty())).Differences);

        Assert.Equal(DiffKind.Added, diff.Kind);
    }

    [Fact]
    public void Collection_without_readable_content_is_not_scripted_but_reported()
    {
        var result = SchemaComparer.Compare(Build(Catalog(content: null)), Build(Empty()));
        var script = TypeScriptGenerator.Generate(result, null, TypeScriptOptions.Default);

        Assert.DoesNotContain("CREATE XML SCHEMA COLLECTION", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key.Kind == ObjectKind.XmlSchemaCollection);
    }

    // --- kapsam ---

    [Fact]
    public void Coverage_probe_marks_collections_as_covered()
    {
        var probe = Assert.Single(CoverageProbe.Probes, p => p.Item == "XML schema collection'lar");
        Assert.True(probe.Covered);
    }

    [Fact]
    public void Change_catalog_names_the_object_type()
    {
        var result = SchemaComparer.Compare(Build(Catalog()), Build(Empty()));
        var change = Assert.Single(ChangeCatalog.Build(result));

        Assert.Equal("XML Schema Collection", change.ObjectType);
        Assert.Equal(ObjectKind.XmlSchemaCollection, ObjectKindLabels.Resolve(change.ObjectType));
    }
}
