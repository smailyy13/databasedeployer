using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Ortama özel kural: <c>TableScriptOptions.NonclusteredIndexFilegroup</c> doluysa üretilen
/// klasik NONCLUSTERED index CREATE'lerinin sonuna <c>ON [&lt;fg&gt;]</c> eklenir. Hem yeni
/// tablo (DisplayScript metni) hem mevcut tabloya index ekleme (CreateIndex) yolu kapsanır.
/// </summary>
public class IndexFilegroupTests
{
    private const int CustomerId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        CustomerId, id, name, "sys", "int", 4, 10, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    private static IndexRow Idx() =>
        new(CustomerId, 2, "IX_A", "NONCLUSTERED", false, false, false, 0, false, false, null);

    private static IndexColumnRow Key() => new(CustomerId, 2, 2, 2, 1, false, false);

    private static CatalogSet Table() => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "Name")],
        Indexes = [Idx()],
        IndexColumns = [Key()],
        IndexExtras = [new IndexExtraRow(CustomerId, 2, false, false)],
    };

    private static CatalogSet TableNoIndex() => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "Name")],
    };

    private static CatalogSet Empty() => new() { DatabaseName = "test", ServerName = "TESTSRV" };

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(CatalogSet source, CatalogSet target, string? filegroup) =>
        TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(source), Build(target)),
            null,
            TableScriptOptions.Default with { NonclusteredIndexFilegroup = filegroup }).Sql;

    [Fact]
    public void New_table_nonclustered_index_gets_filegroup()
    {
        // Kaynakta tablo+index var, hedef boş → "yeni tablo" (DisplayScript yolu).
        Assert.Contains("ON [INDEX_FG]", Script(Table(), Empty(), "INDEX_FG"));
    }

    [Fact]
    public void Index_added_to_existing_table_gets_filegroup()
    {
        // Kaynakta index var, hedefte tablo var ama index yok → index ekleme (CreateIndex yolu).
        var script = Script(Table(), TableNoIndex(), "INDEX_FG");
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_A]", script);
        Assert.Contains("ON [INDEX_FG];", script);
    }

    [Fact]
    public void No_filegroup_when_option_is_null()
    {
        Assert.DoesNotContain("INDEX_FG", Script(Table(), Empty(), null));
        Assert.DoesNotContain("INDEX_FG", Script(Table(), TableNoIndex(), null));
    }
}
