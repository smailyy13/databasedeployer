using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

/// <summary>
/// Arayüz tür adının ObjectKind'e GERİ çevrilebilmesi.
///
/// Ağaçta gösterilen ad ("Full-Text Catalog") kullanıcı kutusunu işaretlediğinde sunucuya
/// geri gelir ve ObjectKind'e çevrilir. Çevrilemezse obje, kullanıcı işaretlemiş olmasına
/// rağmen script'e SESSİZCE girmez — kullanıcının fark etmesi imkânsız bir kayıp.
/// Bu yüzden çevrim her tür için tek tek doğrulanır.
/// </summary>
public class ObjectKindLabelTests
{
    public static TheoryData<ObjectKind> AllKinds
    {
        get
        {
            var data = new TheoryData<ObjectKind>();
            foreach (var kind in Enum.GetValues<ObjectKind>())
                if (kind != ObjectKind.Unknown) data.Add(kind);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllKinds))]
    public void Display_label_resolves_back_to_its_kind(ObjectKind kind)
    {
        var label = ObjectKindLabels.Display(kind);

        Assert.Equal(kind, ObjectKindLabels.Resolve(label));
    }

    [Theory]
    [InlineData("Full-Text Catalog", ObjectKind.FullTextCatalog)]
    [InlineData("User-Defined Type", ObjectKind.UserDefinedType)]
    public void Hyphenated_labels_resolve(string label, ObjectKind expected)
    {
        // Tire yalnız boşluk atılarak çözülemiyordu: bu iki tür seçilse bile script'e girmezdi.
        Assert.Equal(expected, ObjectKindLabels.Resolve(label));
    }

    [Fact]
    public void Every_kind_has_a_distinct_label()
    {
        var labels = Enum.GetValues<ObjectKind>()
            .Where(k => k != ObjectKind.Unknown)
            .Select(ObjectKindLabels.Display)
            .ToList();

        Assert.Equal(labels.Count, labels.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Unknown_label_does_not_silently_become_a_real_kind()
    {
        Assert.Equal(ObjectKind.Unknown, ObjectKindLabels.Resolve("Bilinmeyen Şey"));
    }
}

/// <summary>
/// Script üreteçleri arasındaki sınıf paylaşımı. Bir sınıf tip üretecine eklenip modül
/// üretecinin "başka yerde ele alınıyor" listesine eklenmezse, obje script'e GİRER ama
/// başlıkta "kapsam dışı" diye listelenir — rapor script'le çelişir.
/// </summary>
public class ScriptGeneratorScopeTests
{
    [Fact]
    public void Type_generator_handles_the_database_level_object_kinds()
    {
        Assert.Contains(ObjectKind.FullTextCatalog, SchemaDiff.Core.Scripting.TypeScriptGenerator.HandledKinds);
        Assert.Contains(ObjectKind.PartitionFunction, SchemaDiff.Core.Scripting.TypeScriptGenerator.HandledKinds);
        Assert.Contains(ObjectKind.TableType, SchemaDiff.Core.Scripting.TypeScriptGenerator.HandledKinds);
    }

    [Fact]
    public void Handled_kinds_do_not_overlap_with_module_kinds()
    {
        // Aynı obje iki bölümde birden üretilirse script iki kez CREATE eder.
        var moduleKinds = new[]
        {
            ObjectKind.View, ObjectKind.Procedure, ObjectKind.ScalarFunction,
            ObjectKind.InlineTableFunction, ObjectKind.TableFunction,
            ObjectKind.Trigger, ObjectKind.DdlTrigger,
        };

        Assert.Empty(SchemaDiff.Core.Scripting.TypeScriptGenerator.HandledKinds.Intersect(moduleKinds));
    }
}
