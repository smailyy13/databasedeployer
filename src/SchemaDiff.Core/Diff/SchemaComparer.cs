using System.Diagnostics;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Diff;

/// <summary>
/// İki snapshot'ı hash üzerinden karşılaştırır. Asıl kazanç burada:
/// eşit hash'li objelerin metnine hiç bakılmaz. Gerçek hayatta dev↔prod arasında
/// objelerin %95+'i eşittir, yani pahalı iş yalnızca kalan %5'e uygulanır.
///
/// Yön: kaynak (source) "olması gereken", hedef (target) "deploy edilecek olan".
/// Added  = kaynakta var, hedefte yok  → hedefte oluşturulacak
/// Removed= hedefte var, kaynakta yok  → hedefte silinecek
/// </summary>
public static class SchemaComparer
{
    public static CompareResult Compare(DatabaseSnapshot source, DatabaseSnapshot target)
    {
        var sw = Stopwatch.StartNew();
        var differences = new List<ObjectDiff>();
        var equalCount = 0;

        foreach (var (key, sourceObject) in source.Objects)
        {
            if (!target.Objects.TryGetValue(key, out var targetObject))
            {
                differences.Add(new ObjectDiff(key, DiffKind.Added, []));
                continue;
            }

            if (sourceObject.Incomparable || targetObject.Incomparable)
            {
                differences.Add(new ObjectDiff(key, DiffKind.Indeterminate,
                    [sourceObject.IncomparableReason ?? targetObject.IncomparableReason ?? "bilinmiyor"]));
                continue;
            }

            if (sourceObject.Hash == targetObject.Hash)
            {
                equalCount++;
                continue;
            }

            differences.Add(new ObjectDiff(key, DiffKind.Changed, ChangedParts(sourceObject, targetObject)));
        }

        foreach (var (key, _) in target.Objects)
        {
            if (!source.Objects.ContainsKey(key))
                differences.Add(new ObjectDiff(key, DiffKind.Removed, []));
        }

        differences.Sort(static (a, b) =>
        {
            var byKind = a.Kind.CompareTo(b.Kind);
            if (byKind != 0) return byKind;
            var byType = a.Key.Kind.CompareTo(b.Key.Kind);
            if (byType != 0) return byType;
            var bySchema = string.Compare(a.Key.Schema, b.Key.Schema, StringComparison.OrdinalIgnoreCase);
            return bySchema != 0 ? bySchema
                : string.Compare(a.Key.Name, b.Key.Name, StringComparison.OrdinalIgnoreCase);
        });

        return new CompareResult
        {
            Source = source,
            Target = target,
            Differences = differences,
            EqualCount = equalCount,
            CompareTime = sw.Elapsed,
        };
    }

    /// <summary>
    /// Objenin neresinin değiştiğini alt-parça hash'lerinden okur — metin diff'i
    /// çalıştırmadan "bu tabloda sadece index değişmiş" diyebilmek için.
    /// </summary>
    private static List<string> ChangedParts(ObjectSnapshot source, ObjectSnapshot target)
    {
        var changed = new List<string>();

        foreach (var (part, hash) in source.Parts)
        {
            if (!target.Parts.TryGetValue(part, out var targetHash)) changed.Add(part);
            else if (hash != targetHash) changed.Add(part);
        }

        foreach (var (part, _) in target.Parts)
            if (!source.Parts.ContainsKey(part)) changed.Add(part);

        changed.Sort(StringComparer.Ordinal);
        return changed;
    }
}
