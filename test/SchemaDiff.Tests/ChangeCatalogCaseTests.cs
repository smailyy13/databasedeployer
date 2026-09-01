using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using static SchemaDiff.Tests.TestFactory;

namespace SchemaDiff.Tests;

public class ChangeCatalogCaseTests
{
    // caseSensitiveNames=true iken [T_CASE] ve [t_case] AYRI objelerdir (biri eklenmiş,
    // biri silinmiş). ChangeCatalog risk sözlüğünü karşılaştırmanın kendi karşılaştırıcısıyla
    // kurmalı; sabit CaseInsensitive kullanınca "aynı anahtar iki kez eklendi" ile çökerdi.
    [Fact]
    public void Case_sensitive_key_difference_does_not_crash_and_splits_into_add_and_delete()
    {
        var source = Database("dev",  [Obj(Table("T_CASE"), 1, columns: [Column("A")])], caseSensitive: true);
        var target = Database("prod", [Obj(Table("t_case"), 2, columns: [Column("A")])], caseSensitive: true);

        var result = SchemaComparer.Compare(source, target);
        var changes = ChangeCatalog.Build(result);   // eskiden burada fırlatıyordu

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.Name == "T_CASE" && c.Action == ChangeAction.Add);
        Assert.Contains(changes, c => c.Name == "t_case" && c.Action == ChangeAction.Delete);
    }

    // Case-insensitive (varsayılan) modda anahtarlar birleşir; içerik (hash) aynı ama saklanan
    // ad harf bakımından farklıysa TEK 'Değişti' (ad harf farkı) üretilir — Add+Delete DEĞİL.
    [Fact]
    public void Case_insensitive_name_only_case_difference_is_a_single_change()
    {
        var source = Database("dev",  [Obj(Table("T_CASE"), 1, columns: [Column("A")])]);
        var target = Database("prod", [Obj(Table("t_case"), 1, columns: [Column("A")])]);

        var result = SchemaComparer.Compare(source, target);
        var changes = ChangeCatalog.Build(result);

        var change = Assert.Single(changes);
        Assert.Equal(ChangeAction.Change, change.Action);
        Assert.Equal("T_CASE", change.Name);   // kaynak adı
        Assert.Contains(change.Children, c => c.ItemType == "Name");
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
