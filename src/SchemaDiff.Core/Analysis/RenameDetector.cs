using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Analysis;

/// <summary>Yapısı birebir eşleşen bir silinen+eklenen çift — muhtemelen yeniden adlandırma.</summary>
public sealed record RenameCandidate(ObjectKey Removed, ObjectKey Added, string Basis);

/// <summary>
/// Refactorlog olmadan yeniden adlandırmayı sezgisel bulur: aynı türden bir "silinen" ve bir
/// "eklenen" obje, yapısı BİREBİR aynıysa (tabloda kolon kümesi, modülde ada bağlı olmayan
/// gövde) muhtemelen rename'dir. Amacı UYARMAK — özellikle tabloda drop+create veri kaybettirir,
/// oysa doğrusu sp_rename'dir.
///
/// Otomatik sp_rename ÜRETİLMEZ: yanlış eşleşme (yapısı tesadüfen aynı iki farklı obje) yıkıcı
/// olur. Yalnızca yüksek güvenli, TEK ve karşılıklı eşleşmeler aday sayılır ve kullanıcıya bildirilir.
/// </summary>
public static class RenameDetector
{
    public static IReadOnlyList<RenameCandidate> Detect(CompareResult result)
    {
        var candidates = new List<RenameCandidate>();

        // Tür bazında silinenleri ve eklenenleri topla (yalnızca imza üretebildiğimiz türler).
        var removedByKind = new Dictionary<ObjectKind, List<ObjectKey>>();
        var addedByKind = new Dictionary<ObjectKind, List<ObjectKey>>();

        foreach (var diff in result.Differences)
        {
            var map = diff.Kind switch
            {
                DiffKind.Removed => removedByKind,
                DiffKind.Added => addedByKind,
                _ => null,
            };
            if (map is null || !HasSignature(diff.Key.Kind)) continue;
            if (!map.TryGetValue(diff.Key.Kind, out var list)) map[diff.Key.Kind] = list = [];
            list.Add(diff.Key);
        }

        foreach (var (kind, removed) in removedByKind)
        {
            if (!addedByKind.TryGetValue(kind, out var added)) continue;

            // İmza → o imzaya sahip anahtarlar. Eşleşme TEK ise (her iki tarafta bir tane) aday.
            var removedBySig = GroupBySignature(removed, result.Target);
            var addedBySig = GroupBySignature(added, result.Source);

            foreach (var (sig, removedKeys) in removedBySig)
            {
                if (removedKeys.Count != 1) continue;                    // belirsiz → atla
                if (!addedBySig.TryGetValue(sig, out var addedKeys)) continue;
                if (addedKeys.Count != 1) continue;                      // belirsiz → atla
                if (string.IsNullOrEmpty(sig)) continue;                 // boş imza güvenilmez

                candidates.Add(new RenameCandidate(removedKeys[0], addedKeys[0],
                    kind == ObjectKind.Table ? "kolon yapısı birebir aynı" : "gövde birebir aynı"));
            }
        }

        return candidates;
    }

    private static bool HasSignature(ObjectKind kind) => kind is
        ObjectKind.Table or ObjectKind.View or ObjectKind.Procedure or
        ObjectKind.ScalarFunction or ObjectKind.InlineTableFunction or ObjectKind.TableFunction;

    private static Dictionary<string, List<ObjectKey>> GroupBySignature(
        List<ObjectKey> keys, DatabaseSnapshot snapshot)
    {
        var result = new Dictionary<string, List<ObjectKey>>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            if (!snapshot.Objects.TryGetValue(key, out var obj)) continue;
            var sig = Signature(obj);
            if (!result.TryGetValue(sig, out var list)) result[sig] = list = [];
            list.Add(key);
        }
        return result;
    }

    /// <summary>
    /// Ada bağlı OLMAYAN imza. Tabloda kolon kümesi (kolon adları tablo adını içermez); modülde
    /// gövde metni. Modül gövdesi kendi CREATE ifadesinde adı taşıdığından, adın geçtiği ilk
    /// tanımlayıcı çıkarılarak kaba bir ada-bağımsız imza elde edilir.
    /// </summary>
    private static string Signature(ObjectSnapshot obj)
    {
        if (obj.Key.Kind == ObjectKind.Table)
            return obj.PartCanonical.GetValueOrDefault("columns", string.Empty);

        // Modül: gövdeden objenin kendi adını (şema.ad ve ad) çıkar — yeniden adlandırmada
        // yalnızca bu değişir. Kaba ama yüksek güvenli eşleşme için yeterli.
        var body = obj.PartCanonical.GetValueOrDefault("body", string.Empty);
        return body
            .Replace($"[{obj.Key.Schema}].[{obj.Key.Name}]", "[__self__]", StringComparison.OrdinalIgnoreCase)
            .Replace($"[{obj.Key.Name}]", "[__self__]", StringComparison.OrdinalIgnoreCase);
    }
}
