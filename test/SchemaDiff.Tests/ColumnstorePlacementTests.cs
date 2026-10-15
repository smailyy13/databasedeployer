using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// CLUSTERED COLUMNSTORE index mevcut tabloya eklenince (CreateIndex yolu) depolama
/// yerleşimi (filegroup / partition scheme) KORUNMALI. Eskiden bu dal placement'ı atlıyordu.
/// </summary>
public class ColumnstorePlacementTests
{
    private const int TId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        TId, id, name, "sys", "int", 4, 10, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    // Clustered columnstore index, IDXFG (data_space 2) üzerinde.
    private static CatalogSet WithCci() => new()
    {
        DatabaseName = "t", ServerName = "S",
        Objects = [new ObjectRow(TId, "dbo", "H", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "Dt")],
        Indexes = [new IndexRow(TId, 1, "CCI_H", "CLUSTERED COLUMNSTORE", false, false, false, 0, false, false, null, null, true, true, 2)],
        IndexExtras = [new IndexExtraRow(TId, 1, false, false)],
        DataSpaces = [new DataSpaceRow(1, "PRIMARY", "FG"), new DataSpaceRow(2, "IDXFG", "FG")],
        TablePlacements = [new TablePlacementRow(TId, 1, 2)],
    };

    private static CatalogSet NoCci() => new()
    {
        DatabaseName = "t", ServerName = "S",
        Objects = [new ObjectRow(TId, "dbo", "H", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "Dt")],
    };

    [Fact]
    public void Clustered_columnstore_added_to_existing_table_keeps_placement()
    {
        var sql = TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(WithCci()), Build(NoCci())),
            null, TableScriptOptions.Default).Sql;

        Assert.Contains("CREATE CLUSTERED COLUMNSTORE INDEX [CCI_H]", sql);
        Assert.Contains("ON [IDXFG]", sql);   // yerleşim korunmalı (eskiden atlanıyordu)
    }
}
