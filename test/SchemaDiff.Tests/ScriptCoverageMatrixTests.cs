using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// KAPSAM MATRİSİ: arayüzde fark olarak GÖRÜNEN her obje sınıfı, script üreteç zincirinde
/// ya SQL üretmeli ya da ADIYLA + SEBEBİYLE raporlanmalı.
///
/// Bu testin varlık sebebi Dalga 11 ve 18'de iki kez yaşanan hata: bir sınıf karşılaştırmaya
/// giriyor, arayüzde fark olarak görünüyor, ama hiçbir üreteç onu yazmıyordu — kullanıcı
/// "script üret" deyip değişikliğin uygulandığını sanıyordu. Elle tutulan listeler sürekli
/// ayrıştığı için burada enum'ın TAMAMI üzerinde dönülür: yeni bir ObjectKind eklendiğinde
/// bu test onu otomatik kapsar ve unutulursa kırmızı yanar.
/// </summary>
public class ScriptCoverageMatrixTests
{
    /// <summary>Web'in /api/runs/{id}/script uç noktasındaki dilim sırasının aynısı.</summary>
    private static (string Sql, List<SkippedObject> Skipped, List<ObjectKey> OutOfScope) Chain(CompareResult cmp)
    {
        var sb = new System.Text.StringBuilder();
        var skipped = new List<SkippedObject>();

        var types = TypeScriptGenerator.Generate(cmp, null,
            new TypeScriptOptions { IncludeDrops = true, RecreateChangedTableTypes = true });
        if (!types.IsEmpty) sb.AppendLine(types.Sql);
        skipped.AddRange(types.Skipped);

        var table = TableScriptGenerator.Generate(cmp, null, new TableScriptOptions { AllowDataLoss = true });
        if (!table.IsEmpty) sb.AppendLine(table.Sql);
        skipped.AddRange(table.Skipped);

        var modules = ModuleScriptGenerator.Generate(cmp, null, new ScriptOptions
        {
            TablesHandledElsewhere = true,
            HandledElsewhere = new HashSet<ObjectKind>(TypeScriptGenerator.HandledKinds)
            {
                ObjectKind.Role, ObjectKind.User, ObjectKind.PlanGuide,
            },
        });
        if (!modules.IsEmpty) sb.AppendLine(modules.Sql);
        skipped.AddRange(modules.Skipped);

        var roles = RoleScriptGenerator.Generate(cmp, null, new RoleScriptOptions { IncludeDrops = true });
        if (!roles.IsEmpty) sb.AppendLine(roles.Sql);
        skipped.AddRange(roles.Skipped);

        var ep = ExtendedPropertyScriptGenerator.Generate(cmp, null, null);
        if (!ep.IsEmpty) sb.AppendLine(ep.Sql);
        skipped.AddRange(ep.Skipped);

        var perms = PermissionScriptGenerator.Generate(cmp, null, null);
        if (!perms.IsEmpty) sb.AppendLine(perms.Sql);
        skipped.AddRange(perms.Skipped);

        var settings = SettingsScriptGenerator.Generate(cmp, null, null);
        if (!settings.IsEmpty) sb.AppendLine(settings.Sql);
        skipped.AddRange(settings.Skipped);

        return (sb.ToString(), skipped, [.. modules.OutOfScope]);
    }

    /// <summary>Karşılaştırmaya giren, yani arayüzde satır üreten her sınıf.</summary>
    public static TheoryData<ObjectKind> ComparedKinds()
    {
        var data = new TheoryData<ObjectKind>();
        foreach (var kind in Enum.GetValues<ObjectKind>())
        {
            // Unknown hiç snapshot'a girmez; Database sentetik ve ayrı testleri var.
            if (kind is ObjectKind.Unknown or ObjectKind.Database) continue;
            data.Add(kind);
        }
        return data;
    }

    private static ObjectKey KeyFor(ObjectKind kind) =>
        new(kind == ObjectKind.Schema ? string.Empty : "dbo", $"X_{kind}", kind);

    private static ObjectSnapshot SnapshotFor(ObjectKind kind, UInt128 hash, string body)
    {
        var snapshot = TestFactory.Obj(KeyFor(kind), hash, displayScript: body);
        snapshot.Columns = [new ColumnInfo("Id", "sys", "int", 4, 10, 0, false, null, false, false, null)];
        snapshot.PartCanonical["definition"] = body;
        snapshot.Parts["definition"] = hash;
        return snapshot;
    }

    [Theory]
    [MemberData(nameof(ComparedKinds))]
    public void Added_object_of_every_kind_reaches_the_script(ObjectKind kind)
    {
        var cmp = SchemaComparer.Compare(
            TestFactory.Database("src", [SnapshotFor(kind, 1, $"CREATE {kind} [dbo].[X_{kind}] AS SELECT 1")]),
            TestFactory.Database("tgt", []));

        var (sql, _, _) = Chain(cmp);

        Assert.True(sql.Length > 0,
            $"{kind} arayüzde 'Add' olarak görünür ama hiçbir üreteç onu yazmıyor.");
    }

    /// <summary>
    /// Silinen obje ya DROP üretmeli ya da adıyla raporlanmalı. TEK istisna şema:
    /// <c>DROP SCHEMA</c> yalnız şema boşken çalışır ve içindeki objelerin drop sırasına
    /// bağlıdır — bilinçli olarak üretilmiyor, "kapsam dışı" olarak sayılıyor.
    /// </summary>
    [Theory]
    [MemberData(nameof(ComparedKinds))]
    public void Removed_object_of_every_kind_reaches_the_script(ObjectKind kind)
    {
        var cmp = SchemaComparer.Compare(
            TestFactory.Database("src", []),
            TestFactory.Database("tgt", [SnapshotFor(kind, 1, $"CREATE {kind} [dbo].[X_{kind}] AS SELECT 1")]));

        var (sql, skipped, outOfScope) = Chain(cmp);

        if (kind == ObjectKind.Schema)
        {
            Assert.Empty(sql);
            Assert.Contains(KeyFor(kind), outOfScope);
            return;
        }

        Assert.True(sql.Length > 0 || skipped.Any(s => s.Key == KeyFor(kind)),
            $"{kind} arayüzde 'Delete' olarak görünür ama ne DROP üretiliyor ne de sebebi raporlanıyor.");
    }

    /// <summary>
    /// Değişen obje SQL üretmiyorsa, sebebi ADIYLA raporlanmalı — sessizce düşmemeli.
    /// ALTER edilemeyen sınıflarda (sequence, synonym, alias tip, partition, XML koleksiyon…)
    /// beklenen davranış zaten "atla ve söyle"dir; test edilen şey SÖYLENDİĞİDİR.
    /// </summary>
    [Theory]
    [MemberData(nameof(ComparedKinds))]
    public void Changed_object_of_every_kind_is_scripted_or_named(ObjectKind kind)
    {
        if (kind == ObjectKind.Schema) return;   // şemanın "definition" parçası adından türer, değişemez

        var cmp = SchemaComparer.Compare(
            TestFactory.Database("src", [SnapshotFor(kind, 1, $"CREATE {kind} [dbo].[X_{kind}] AS SELECT 1")]),
            TestFactory.Database("tgt", [SnapshotFor(kind, 2, $"CREATE {kind} [dbo].[X_{kind}] AS SELECT 2")]));

        var (sql, skipped, _) = Chain(cmp);

        Assert.True(sql.Length > 0 || skipped.Any(s => s.Key == KeyFor(kind) && s.Reason.Length > 0),
            $"{kind} arayüzde 'Change' olarak görünür ama ne script'e giriyor ne de sebebi raporlanıyor.");
    }

    /// <summary>
    /// Şemanın gerçek hayatta değişebilen tek parçaları extended property ve izinlerdir
    /// ("definition" parçası şema adından türer). İkisi de kendi üreteçlerine ulaşmalı.
    /// </summary>
    [Theory]
    [InlineData("extendedProperties", "ep|1|3|RPT||||MS_Description|Raporlar")]
    [InlineData("permissions", "perm|schema|GRANT SELECT TO app_user")]
    public void Changed_schema_reaches_the_script_through_its_parts(string part, string canonical)
    {
        var key = new ObjectKey("RPT", "RPT", ObjectKind.Schema);

        ObjectSnapshot Schema(string? value)
        {
            var snapshot = TestFactory.Obj(key, value is null ? 1u : 2u, displayScript: "CREATE SCHEMA [RPT];");
            snapshot.PartCanonical["definition"] = "schema|RPT";
            snapshot.Parts["definition"] = 1;
            if (value is not null)
            {
                snapshot.PartCanonical[part] = value;
                snapshot.Parts[part] = 2;
            }
            return snapshot;
        }

        var cmp = SchemaComparer.Compare(
            TestFactory.Database("src", [Schema(canonical)]), TestFactory.Database("tgt", [Schema(null)]));

        Assert.True(Chain(cmp).Sql.Length > 0, $"şemanın {part} farkı script'e ulaşmıyor");
    }
}
