using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// INDEX_FG override'ının kenar durumları: kaynaktaki mevcut yerleşimi DEĞİŞTİRMESİ,
/// columnstore'u atlaması, ve filtre metninde ';' olduğunda bozulmaması (regex ';' riski).
/// </summary>
public class IndexFilegroupEdgeTests
{
    private const int TId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        TId, id, name, "sys", "int", 4, 10, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    private static IndexColumnRow Key() => new(TId, 2, 2, 2, 1, false, false);

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(CatalogSet source) =>
        TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(source), Build(new() { DatabaseName = "t", ServerName = "S" })),
            null,
            TableScriptOptions.Default with { NonclusteredIndexFilegroup = "INDEX_FG" }).Sql;

    private static CatalogSet Base(IndexRow idx) => new()
    {
        DatabaseName = "t", ServerName = "S",
        Objects = [new ObjectRow(TId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "Name")],
        Indexes = [idx],
        IndexColumns = [Key()],
        IndexExtras = [new IndexExtraRow(TId, 2, false, false)],
    };

    [Fact]
    public void Override_replaces_existing_source_placement()
    {
        // Kaynak index IDXFG (data_space 2) üzerinde. Override ile INDEX_FG'ye TAŞINMALI,
        // eski IDXFG yerleşimi index satırından kalkmalı.
        var c = Base(new IndexRow(TId, 2, "IX_A", "NONCLUSTERED", false, false, false, 0, false, false, null, null, true, true, 2));
        c.DataSpaces = [new DataSpaceRow(1, "PRIMARY", "FG"), new DataSpaceRow(2, "IDXFG", "FG")];
        c.TablePlacements = [new TablePlacementRow(TId, 0, 1)];

        var sql = Script(c);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_A]", sql);
        Assert.Contains("ON [INDEX_FG]", sql);
        // Index CREATE'i IDXFG'yi ARTIK içermemeli (yalnız tablo/diğer yerlerde değil, index satırında).
        var idxLine = sql.Split('\n').First(l => l.Contains("ON [INDEX_FG]"));
        Assert.DoesNotContain("IDXFG", idxLine);
    }

    [Fact]
    public void Override_skips_nonclustered_columnstore()
    {
        // NONCLUSTERED COLUMNSTORE override'a girmemeli (ON [INDEX_FG] eklenmemeli).
        var c = Base(new IndexRow(TId, 2, "IX_CS", "NONCLUSTERED COLUMNSTORE", false, false, false, 0, false, false, null));
        var sql = Script(c);
        Assert.Contains("COLUMNSTORE", sql);
        Assert.DoesNotContain("ON [INDEX_FG]", sql);
    }

    [Fact]
    public void Override_keeps_filter_with_semicolon_intact()
    {
        // Filtre metninde ';' (string içinde) varsa INDEX_FG enjeksiyonu filtreyi bozmamalı.
        var c = Base(new IndexRow(TId, 2, "IX_F", "NONCLUSTERED", false, false, false, 0, false, false, "([Name]='a;b')"));
        var sql = Script(c);
        Assert.Contains("'a;b'", sql);          // filtre bozulmadan durmalı
        Assert.Contains("ON [INDEX_FG]", sql);  // yerleşim yine de eklenmeli
    }
}
