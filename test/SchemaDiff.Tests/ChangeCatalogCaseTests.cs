using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using static SchemaDiff.Tests.TestFactory;

namespace SchemaDiff.Tests;

public class ChangeCatalogCaseTests
{
    // "Ad büyük/küçük harf duyarlı" AÇIKken [T_CASE] ile [t_case] eşleşir (anahtarlar her zaman
    // harfe duyarsız) ama ad harf farkı bir fark sayılır: TEK bir 'Değişti' (ad) üretilir —
    // Add+Delete DEĞİL. ChangeCatalog kaynak → hedef adını bir "Name" alt satırında gösterir.
    [Fact]
    public void Case_sensitive_name_only_case_difference_is_a_single_change()
    {
        var source = Database("dev",  [Obj(Table("T_CASE"), 1, columns: [Column("A")])], caseSensitive: true);
        var target = Database("prod", [Obj(Table("t_case"), 1, columns: [Column("A")])], caseSensitive: true);

        var result = SchemaComparer.Compare(source, target);
        var changes = ChangeCatalog.Build(result);

        var change = Assert.Single(changes);
        Assert.Equal(ChangeAction.Change, change.Action);
        Assert.Equal("T_CASE", change.Name);   // kaynak adı
        Assert.Contains(change.Children, c => c.ItemType == "Name");
    }

    // "Ad büyük/küçük harf duyarlı" KAPALIyken (varsayılan) ad harf farkı yok sayılır:
    // [T_CASE] ile [t_case] eşit sayılır, hiç listelenmez.
    [Fact]
    public void Case_insensitive_name_only_case_difference_is_not_listed()
    {
        var source = Database("dev",  [Obj(Table("T_CASE"), 1, columns: [Column("A")])]);
        var target = Database("prod", [Obj(Table("t_case"), 1, columns: [Column("A")])]);

        var result = SchemaComparer.Compare(source, target);

        Assert.Empty(ChangeCatalog.Build(result));
    }

    // İçerik de ad da aynıysa (harf dahil) hiç fark yok.
    [Fact]
    public void Identical_name_and_content_is_equal()
    {
        var source = Database("dev",  [Obj(Table("Same"), 1, columns: [Column("A")])]);
        var target = Database("prod", [Obj(Table("Same"), 1, columns: [Column("A")])]);

        Assert.Empty(ChangeCatalog.Build(SchemaComparer.Compare(source, target)));
    }
}
