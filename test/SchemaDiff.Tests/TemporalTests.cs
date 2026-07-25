using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

public class TemporalTests
{
    private const int TableId = 100;

    private static CatalogSet Table(TemporalRow? temporal, params ColumnRow[] columns) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(TableId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [.. columns],
        Temporal = temporal is null ? [] : [temporal],
    };

    private static ColumnRow Col(int id, string name) => new(
        TableId, id, name, "sys", "int", 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null);

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), SnapshotOptions.Default);

    [Fact]
    public void Table_becoming_temporal_is_detected()
    {
        var source = Build(Table(new TemporalRow(TableId, "dbo", "CustomerHistory", "ValidFrom", "ValidTo"), Col(1, "Id")));
        var target = Build(Table(null, Col(1, "Id")));

        var result = SchemaComparer.Compare(source, target);
        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("temporal", diff.ChangedParts);

        var change = Assert.Single(ChangeCatalog.Build(result));
        var child = Assert.Single(change.Children, c => c.ItemType == "System Versioning");
        Assert.Contains("açılıyor", child.Detail);
    }

    [Fact]
    public void Identical_temporal_setup_produces_no_difference()
    {
        var t = new TemporalRow(TableId, "dbo", "CustomerHistory", "ValidFrom", "ValidTo");
        var source = Build(Table(t, Col(1, "Id")));
        var target = Build(Table(t, Col(1, "Id")));

        var result = SchemaComparer.Compare(source, target);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Auto_generated_history_name_is_normalized_to_avoid_false_positive()
    {
        // İki ortamda otomatik history adları object_id yüzünden farklı — fark ÜRETMEMELİ.
        var source = Build(Table(new TemporalRow(TableId, "dbo", "MSSQL_TemporalHistoryFor_111", "S", "E"), Col(1, "Id")));
        var target = Build(Table(new TemporalRow(TableId, "dbo", "MSSQL_TemporalHistoryFor_999", "S", "E"), Col(1, "Id")));

        var result = SchemaComparer.Compare(source, target);
        Assert.Empty(result.Differences);
    }

    [Fact]
    public void Changed_period_columns_are_detected()
    {
        var source = Build(Table(new TemporalRow(TableId, "dbo", "H", "SysStart", "SysEnd"), Col(1, "Id")));
        var target = Build(Table(new TemporalRow(TableId, "dbo", "H", "ValidFrom", "ValidTo"), Col(1, "Id")));

        var result = SchemaComparer.Compare(source, target);
        Assert.Single(result.Differences, d => d.ChangedParts.Contains("temporal"));
    }

    [Fact]
    public void History_tables_are_excluded_from_comparison()
    {
        // History tablosu (temporal_type=1) Objects sorgusundan zaten çıkarılıyor; burada
        // temporal tablonun kendi karşılaştırması test ediliyor. Yalnızca 1 obje olmalı.
        var source = Build(Table(new TemporalRow(TableId, "dbo", "H", "S", "E"), Col(1, "Id")));
        // Yalnızca temporal tablonun kendisi + sentetik "(database)" objesi (history hariç).
        Assert.Contains(source.Objects.Keys, k => k.Kind == ObjectKind.Table && k.Name == "Customer");
        Assert.DoesNotContain(source.Objects.Keys, k => k.Name == "H");
    }
}
