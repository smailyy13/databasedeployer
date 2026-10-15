using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// ALTER COLUMN, kolonu içeren DEĞİŞMEYEN bir index tarafından SQL'de kilitlenir
/// (Msg 5074). Üretilen script bu index'i ALTER'dan ÖNCE düşüp SONRA yeniden kurmalı.
/// </summary>
public class AlterColumnIndexTests
{
    private const int TId = 100;
    private static readonly SnapshotOptions Opts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int cid, string name, string type, short len, byte prec) =>
        new(TId, cid, name, "sys", type, len, prec, 0, true, null, false, false,
            null, null, null, null, null, null, null);

    // Keep kolonunun tipi parametrik; IX_Keep her iki tarafta da Keep üzerinde (değişmez).
    private static CatalogSet Table(string keepType, short keepLen, byte keepPrec) => new()
    {
        DatabaseName = "t", ServerName = "S",
        Objects = [new ObjectRow(TId, "dbo", "T", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id", "int", 4, 10), Col(2, "Keep", keepType, keepLen, keepPrec)],
        Indexes = [new IndexRow(TId, 2, "IX_Keep", "NONCLUSTERED", false, false, false, 0, false, false, null)],
        IndexColumns = [new IndexColumnRow(TId, 2, 1, 2, 1, false, false)],
        IndexExtras = [new IndexExtraRow(TId, 2, false, false)],
    };

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), Opts);

    [Fact]
    public void Unchanged_index_on_altered_column_is_dropped_then_recreated()
    {
        var source = Build(Table("bigint", 8, 19));   // Keep: int → bigint
        var target = Build(Table("int", 4, 10));

        var sql = TableScriptGenerator.Generate(
            SchemaComparer.Compare(source, target), null,
            TableScriptOptions.Default with { AllowDataLoss = true }).Sql;

        var dropAt = sql.IndexOf("DROP INDEX [IX_Keep]", StringComparison.Ordinal);
        var alterAt = sql.IndexOf("ALTER COLUMN [Keep]", StringComparison.Ordinal);
        var createAt = sql.IndexOf("CREATE NONCLUSTERED INDEX [IX_Keep]", StringComparison.Ordinal);

        Assert.True(dropAt >= 0, "IX_Keep ALTER'dan önce düşürülmeli");
        Assert.True(alterAt >= 0, "Keep ALTER COLUMN üretilmeli");
        Assert.True(createAt >= 0, "IX_Keep yeniden oluşturulmalı");
        Assert.True(dropAt < alterAt, "DROP INDEX, ALTER COLUMN'dan ÖNCE gelmeli (Msg 5074)");
        Assert.True(alterAt < createAt, "CREATE INDEX, ALTER COLUMN'dan SONRA gelmeli");
    }

    [Fact]
    public void Unchanged_index_untouched_when_its_column_is_not_altered()
    {
        // Keep değişmiyorsa IX_Keep'e dokunulmamalı (gereksiz drop/recreate olmasın).
        var source = Build(Table("int", 4, 10));
        var target = Build(Table("int", 4, 10));
        var diffs = SchemaComparer.Compare(source, target).Differences;
        Assert.Empty(diffs);
    }
}
