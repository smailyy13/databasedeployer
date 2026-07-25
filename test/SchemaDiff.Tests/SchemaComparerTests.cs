using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using static SchemaDiff.Tests.TestFactory;

namespace SchemaDiff.Tests;

public class SchemaComparerTests
{
    [Fact]
    public void Object_only_in_source_is_Added()
    {
        // Kaynak = olması gereken (dev), hedef = deploy edilecek (prod).
        // Kaynakta var, hedefte yok → hedefte oluşturulacak = Added.
        var source = Database("dev", [Obj(View("NewView"), 1)]);
        var target = Database("prod", []);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Added, diff.Kind);
        Assert.Equal("NewView", diff.Key.Name);
    }

    [Fact]
    public void Object_only_in_target_is_Removed()
    {
        var source = Database("dev", []);
        var target = Database("prod", [Obj(Proc("LegacyProc"), 1)]);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Removed, diff.Kind);
        Assert.Equal("LegacyProc", diff.Key.Name);
    }

    [Fact]
    public void Equal_hash_produces_no_difference_and_counts_as_equal()
    {
        var key = View("SameView");
        var source = Database("dev", [Obj(key, 42)]);
        var target = Database("prod", [Obj(key, 42)]);

        var result = SchemaComparer.Compare(source, target);

        Assert.Empty(result.Differences);
        Assert.Equal(1, result.EqualCount);
    }

    [Fact]
    public void Different_hash_produces_Changed()
    {
        var key = Proc("P");
        var source = Database("dev", [Obj(key, 1)]);
        var target = Database("prod", [Obj(key, 2)]);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
    }

    [Fact]
    public void Incomparable_object_is_Indeterminate_not_Equal()
    {
        // Sessiz eşitlik en tehlikeli sonuç: okunamayan obje ayrı raporlanmalı.
        var key = Proc("Encrypted");
        var source = Database("dev", [Obj(key, 1, incomparable: true)]);
        var target = Database("prod", [Obj(key, 1, incomparable: true)]);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Indeterminate, diff.Kind);
    }

    [Fact]
    public void Changed_parts_are_reported_from_subpart_hashes()
    {
        var key = Table("T");
        var src = Obj(key, 1);
        src.Parts["columns"] = 10;
        src.Parts["indexes"] = 20;
        var tgt = Obj(key, 2);
        tgt.Parts["columns"] = 10;   // aynı
        tgt.Parts["indexes"] = 99;   // farklı

        var source = Database("dev", [src]);
        var target = Database("prod", [tgt]);

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Equal(["indexes"], diff.ChangedParts);
    }

    [Fact]
    public void Differences_are_grouped_by_kind_added_before_removed()
    {
        var source = Database("dev", [Obj(View("A"), 1)]);
        var target = Database("prod", [Obj(Proc("Z"), 1)]);

        var result = SchemaComparer.Compare(source, target);

        Assert.Equal(1, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        // Added, DiffKind sıralamasında Removed'dan önce gelir.
        Assert.Equal(DiffKind.Added, result.Differences[0].Kind);
    }
}
