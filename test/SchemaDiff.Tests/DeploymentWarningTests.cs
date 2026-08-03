using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using static SchemaDiff.Tests.TestFactory;

namespace SchemaDiff.Tests;

public class DeploymentWarningTests
{
    private static (int Count, string Text) Build(DatabaseSnapshot source, DatabaseSnapshot target,
        ISet<ObjectKey>? selection = null)
    {
        var cmp = SchemaComparer.Compare(source, target);
        return DeploymentWarning.Build([(cmp, selection)]);
    }

    [Fact]
    public void No_blocking_table_yields_empty_warning()
    {
        var key = Table("T");
        // Nullable kolon ekleme → güvenli, uyarı çıkmamalı.
        var (count, text) = Build(
            Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("Note", nullable: true)])]),
            Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 1000)]));

        Assert.Equal(0, count);
        Assert.Equal(string.Empty, text);
    }

    [Fact]
    public void Certain_block_is_labelled_BLOKLANACAK()
    {
        var key = Table("Musteri");
        var (count, text) = Build(
            Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("VergiNo", nullable: false)])]),
            Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 3)]));

        Assert.Equal(1, count);
        Assert.Contains("DEPLOYMENT UYARISI", text);
        Assert.Contains("BLOKLANACAK", text);
        Assert.Contains("[dbo].[Musteri]", text);
        Assert.Contains("VergiNo", text);
    }

    [Fact]
    public void Conditional_only_block_is_labelled_KONTROL_ET()
    {
        var key = Table("Musteri");
        var (count, text) = Build(
            Database("dev", [Obj(key, 1, columns: [Column("Id")],
                indexes: "idx|UQ_Email|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Email ASC|include=")]),
            Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 3)]));

        Assert.Equal(1, count);
        Assert.Contains("KONTROL ET", text);
    }

    [Fact]
    public void Selection_filters_out_unselected_tables()
    {
        var key = Table("Musteri");
        var other = Table("Baska");
        var source = Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("VergiNo", nullable: false)])]);
        var target = Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 3)]);

        // Yalnızca seçili olmayan bir tabloyu seçersek → uyarı boş.
        var sel = new HashSet<ObjectKey>(ObjectKeyComparer.CaseInsensitive) { other };
        var (count, _) = Build(source, target, sel);

        Assert.Equal(0, count);
    }

    [Fact]
    public void Unknown_row_count_is_shown_in_text()
    {
        var key = Table("T");
        var (count, text) = Build(
            Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("Req", nullable: false)])]),
            Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: null)]));

        Assert.Equal(1, count);
        Assert.Contains("satır sayısı okunamadı", text);
    }

    [Fact]
    public void Multiple_tables_are_all_counted()
    {
        var a = Table("A");
        var b = Table("B");
        var source = Database("dev", [
            Obj(a, 1, columns: [Column("Id"), Column("Req", nullable: false)]),
            Obj(b, 1, columns: [Column("Id")],
                indexes: "idx|UQ|NONCLUSTERED|unique=1|pk=0|uq=1|keys=Id ASC|include="),
        ]);
        var target = Database("prod", [
            Obj(a, 2, columns: [Column("Id")], rowCount: 5),
            Obj(b, 2, columns: [Column("Id")], rowCount: 5),
        ]);

        var (count, text) = Build(source, target);
        Assert.Equal(2, count);
        Assert.Contains("[dbo].[A]", text);
        Assert.Contains("[dbo].[B]", text);
    }

    [Fact]
    public void Multiple_segments_sum_their_blocking_tables()
    {
        // İki ayrı gövde (ileri + ters gibi) → sayılar toplanır.
        var key = Table("T");
        var fwdCmp = SchemaComparer.Compare(
            Database("dev", [Obj(key, 1, columns: [Column("Id"), Column("Req", nullable: false)])]),
            Database("prod", [Obj(key, 2, columns: [Column("Id")], rowCount: 3)]));
        var revKey = Table("R");
        var revCmp = SchemaComparer.Compare(
            Database("prod", [Obj(revKey, 1, columns: [Column("Id"), Column("Req2", nullable: false)])]),
            Database("dev", [Obj(revKey, 2, columns: [Column("Id")], rowCount: 8)]));

        var (count, _) = DeploymentWarning.Build([(fwdCmp, null), (revCmp, null)]);
        Assert.Equal(2, count);
    }
}
