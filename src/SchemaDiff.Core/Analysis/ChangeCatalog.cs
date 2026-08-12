using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Analysis;

/// <summary>SSDT'nin sonuç ağacındaki üst gruplar.</summary>
public enum ChangeAction { Add, Change, Delete }

/// <summary>
/// Bir objenin içindeki tekil öğe. <paramref name="Category"/> ağaçtaki klasördür
/// ("Columns", "Indexes", "Primary Key"…), <paramref name="ItemType"/> ise öğenin türü.
/// İkisi de İngilizce tutulur — SSDT çıktısıyla aynı okunsun.
/// </summary>
public sealed record ChildChange(
    ChangeAction Action,
    string Category,
    string ItemType,
    string Name,
    string QualifiedName,
    string? Detail);

public sealed record ObjectChange(
    ChangeAction Action,
    string ObjectType,
    string Schema,
    string Name,
    IReadOnlyList<ChildChange> Children,
    DeploymentRisk Risk,
    long? TargetRowCount,
    bool WillBlock,
    bool ConditionalOnly,
    bool Indeterminate,
    string? Note);

/// <summary>
/// Karşılaştırma sonucunu SSDT'nin gösterdiği ağaca çevirir: eylem (Add/Change/Delete)
/// ve obje türüne göre kategorize, alt kırılımıyla birlikte.
///
/// Eklenen ve silinen objeler de açılabilir: SSDT silinecek bir tabloyu tıklayınca
/// içindeki kolonları, PK'sını ve index'lerini gösterir. "Neyi kaybediyorum?" sorusunun
/// cevabı orada.
/// </summary>
public static class ChangeCatalog
{
    public static IReadOnlyList<ObjectChange> Build(CompareResult result)
    {
        var risks = DeploymentRiskAnalyzer.Analyze(result)
            .ToDictionary(r => r.Table, r => r, ObjectKeyComparer.CaseInsensitive);

        var changes = new List<ObjectChange>(result.Differences.Count);

        foreach (var diff in result.Differences)
        {
            result.Source.Objects.TryGetValue(diff.Key, out var source);
            result.Target.Objects.TryGetValue(diff.Key, out var target);
            risks.TryGetValue(diff.Key, out var risk);

            var (action, indeterminate, note) = diff.Kind switch
            {
                DiffKind.Added => (ChangeAction.Add, false, (string?)null),
                DiffKind.Removed => (ChangeAction.Delete, false, null),
                DiffKind.Indeterminate => (ChangeAction.Change, true,
                    diff.ChangedParts.Count > 0 ? diff.ChangedParts[0] : "karşılaştırılamadı"),
                _ => (ChangeAction.Change, false, null),
            };

            var children = (action, indeterminate) switch
            {
                (ChangeAction.Add, _) => Enumerate(diff.Key, source, ChangeAction.Add),
                (ChangeAction.Delete, _) => Enumerate(diff.Key, target, ChangeAction.Delete),
                (_, false) => BuildChildren(diff, source, target),
                _ => [],
            };

            changes.Add(new ObjectChange(
                action,
                TypeName(diff.Key.Kind),
                diff.Key.Schema,
                diff.Key.Name,
                children,
                risk?.Risk ?? DeploymentRisk.Safe,
                risk?.TargetRowCount ?? target?.RowCount,
                risk?.WillBlock ?? false,
                risk?.ConditionalOnly ?? false,
                indeterminate,
                note));
        }

        return changes;
    }

    // --- eklenen / silinen objenin içeriğini dök ---

    private static IReadOnlyList<ChildChange> Enumerate(
        ObjectKey key, ObjectSnapshot? snapshot, ChangeAction action)
    {
        var children = new List<ChildChange>();
        if (snapshot is null) return children;

        foreach (var column in snapshot.Columns ?? [])
        {
            children.Add(new ChildChange(action, "Columns", "Column", column.Name,
                $"{key.Schema}.{key.Name}.{column.Name}",
                $"{column.TypeDisplay} {(column.IsNullable ? "NULL" : "NOT NULL")}"));
        }

        foreach (var part in new[] { "indexes", "statistics", "xmlIndexes", "spatialIndexes", "fullText", "checks", "foreignKeys" })
        {
            foreach (var (name, line) in LinesByName(snapshot.PartCanonical.GetValueOrDefault(part)))
            {
                var (category, itemType) = Classify(part, line);
                children.Add(new ChildChange(action, category, itemType, name,
                    $"{key.Schema}.{name}", Summarize(line)));
            }
        }

        if (snapshot.PartCanonical.ContainsKey("body"))
            children.Add(new ChildChange(action, "Properties", "Body", "tanım",
                $"{key.Schema}.{key.Name}", null));

        if (snapshot.PartCanonical.ContainsKey("temporal"))
            children.Add(new ChildChange(action, "Properties", "System Versioning", "temporal",
                $"{key.Schema}.{key.Name}", "SYSTEM_VERSIONING = ON"));

        foreach (var (id, value) in ExtendedMap(snapshot.PartCanonical.GetValueOrDefault("extendedProperties")))
        {
            var (scope, name) = SplitEpId(id);
            children.Add(new ChildChange(action, "Extended Properties", "Extended Property", name,
                $"{key.Schema}.{key.Name} · {scope}", TrimValue(value)));
        }

        foreach (var (_, entry) in PermissionMap(snapshot.PartCanonical.GetValueOrDefault("permissions")))
            children.Add(new ChildChange(action, "Permissions", "Permission", entry.Display,
                $"{key.Schema}.{key.Name} · {entry.Scope}", null));

        // Rol üyeleri (rol objesi eklendiğinde/silindiğinde).
        foreach (var member in MemberList(snapshot.PartCanonical.GetValueOrDefault("members")))
            children.Add(new ChildChange(action, "Membership", "Member", member, $"{key.Name}\\{member}", null));

        return children;
    }

    // --- değişen objenin farkları ---

    private static IReadOnlyList<ChildChange> BuildChildren(
        ObjectDiff diff, ObjectSnapshot? source, ObjectSnapshot? target)
    {
        var children = new List<ChildChange>();
        if (source is null || target is null) return children;

        var key = diff.Key;

        foreach (var part in diff.ChangedParts)
        {
            switch (part)
            {
                case "columns":
                    children.AddRange(CompareColumns(key, source.Columns, target.Columns));
                    break;

                case "indexes":
                case "statistics":
                case "xmlIndexes":
                case "spatialIndexes":
                case "fullText":
                case "checks":
                case "foreignKeys":
                    children.AddRange(CompareLines(key, source, target, part));
                    break;

                case "body":
                    children.Add(new ChildChange(ChangeAction.Change, "Properties", "Body", "tanım",
                        $"{key.Schema}.{key.Name}", "Gövde metni değişti"));
                    break;

                case "setOptions":
                    children.Add(new ChildChange(ChangeAction.Change, "Properties", "Set Options",
                        "ANSI_NULLS / QUOTED_IDENTIFIER", $"{key.Schema}.{key.Name}", null));
                    break;

                case "temporal":
                    children.Add(new ChildChange(ChangeAction.Change, "Properties", "System Versioning",
                        "temporal ayarı", $"{key.Schema}.{key.Name}",
                        DescribeTemporalChange(source.PartCanonical.GetValueOrDefault("temporal"),
                                               target.PartCanonical.GetValueOrDefault("temporal"))));
                    break;

                case "attributes":
                    children.Add(new ChildChange(ChangeAction.Change, "Properties", "State",
                        "etkin/pasif", $"{key.Schema}.{key.Name}", null));
                    break;

                case "extendedProperties":
                    children.AddRange(CompareExtendedProperties(key, source, target));
                    break;

                case "permissions":
                    children.AddRange(ComparePermissions(key, source, target));
                    break;

                case "members":
                    children.AddRange(CompareMembers(key, source, target));
                    break;

                case "owner":
                    children.Add(new ChildChange(ChangeAction.Change, "Properties", "Owner",
                        "sahip", key.Name, null));
                    break;

                default:
                    children.Add(new ChildChange(ChangeAction.Change, "Properties", part, part,
                        $"{key.Schema}.{key.Name}", null));
                    break;
            }
        }

        return children;
    }

    private static IEnumerable<ChildChange> CompareColumns(
        ObjectKey key, IReadOnlyList<ColumnInfo>? source, IReadOnlyList<ColumnInfo>? target)
    {
        if (source is null || target is null) yield break;

        var targetByName = target.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var sourceByName = source.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var column in source)
        {
            var qualified = $"{key.Schema}.{key.Name}.{column.Name}";

            if (!targetByName.TryGetValue(column.Name, out var existing))
            {
                yield return new ChildChange(ChangeAction.Add, "Columns", "Column", column.Name, qualified,
                    $"{column.TypeDisplay} {(column.IsNullable ? "NULL" : "NOT NULL")}");
                continue;
            }

            var detail = DescribeColumnChange(existing, column);
            if (detail is not null)
                yield return new ChildChange(ChangeAction.Change, "Columns", "Column", column.Name, qualified, detail);
        }

        foreach (var column in target)
        {
            if (!sourceByName.ContainsKey(column.Name))
                yield return new ChildChange(ChangeAction.Delete, "Columns", "Column", column.Name,
                    $"{key.Schema}.{key.Name}.{column.Name}",
                    $"{column.TypeDisplay} {(column.IsNullable ? "NULL" : "NOT NULL")}");
        }
    }

    /// <param name="target">Şu anki hedef hâli.</param>
    /// <param name="source">Olması gereken hâl.</param>
    private static string? DescribeColumnChange(ColumnInfo target, ColumnInfo source)
    {
        var parts = new List<string>();

        if (!string.Equals(target.TypeDisplay, source.TypeDisplay, StringComparison.OrdinalIgnoreCase))
            parts.Add($"{target.TypeDisplay} → {source.TypeDisplay}");

        if (target.IsNullable != source.IsNullable)
            parts.Add(target.IsNullable ? "NULL → NOT NULL" : "NOT NULL → NULL");

        if (target.IsIdentity != source.IsIdentity)
            parts.Add(source.IsIdentity ? "IDENTITY ekleniyor" : "IDENTITY kaldırılıyor");

        if (target.IsComputed != source.IsComputed)
            parts.Add(source.IsComputed ? "computed oluyor" : "computed olmaktan çıkıyor");

        // Depolama nitelikleri: kanonikte fark üretirler, burada da adlarıyla görünmeliler —
        // aksi hâlde tablo "değişti" görünür ama hiçbir alt satır sebebi göstermez.
        foreach (var (name, from, to) in new[]
                 {
                     ("SPARSE", target.IsSparse, source.IsSparse),
                     ("FILESTREAM", target.IsFileStream, source.IsFileStream),
                     ("ROWGUIDCOL", target.IsRowGuidCol, source.IsRowGuidCol),
                     ("COLUMN_SET", target.IsColumnSet, source.IsColumnSet),
                 })
        {
            if (from != to) parts.Add(to ? $"{name} ekleniyor" : $"{name} kaldırılıyor");
        }

        if (!string.Equals(target.Collation, source.Collation, StringComparison.OrdinalIgnoreCase))
            parts.Add($"collation {target.Collation ?? "-"} → {source.Collation ?? "-"}");

        if (!string.Equals(target.DefaultDefinition, source.DefaultDefinition, StringComparison.Ordinal))
            parts.Add($"DEFAULT {target.DefaultDefinition ?? "yok"} → {source.DefaultDefinition ?? "yok"}");

        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    private static IEnumerable<ChildChange> CompareLines(
        ObjectKey key, ObjectSnapshot source, ObjectSnapshot target, string part)
    {
        var sourceLines = LinesByName(source.PartCanonical.GetValueOrDefault(part));
        var targetLines = LinesByName(target.PartCanonical.GetValueOrDefault(part));

        foreach (var (name, line) in sourceLines)
        {
            var (category, itemType) = Classify(part, line);
            var qualified = $"{key.Schema}.{name}";

            if (!targetLines.TryGetValue(name, out var existing))
            {
                yield return new ChildChange(ChangeAction.Add, category, itemType, name, qualified, Summarize(line));
                continue;
            }

            if (!string.Equals(existing, line, StringComparison.Ordinal))
                yield return new ChildChange(ChangeAction.Change, category, itemType, name, qualified,
                    DescribeLineChange(existing, line));
        }

        foreach (var (name, line) in targetLines)
        {
            if (sourceLines.ContainsKey(name)) continue;
            var (category, itemType) = Classify(part, line);
            yield return new ChildChange(ChangeAction.Delete, category, itemType, name,
                $"{key.Schema}.{name}", Summarize(line));
        }
    }

    /// <summary>
    /// Extended property'leri (host'un "extendedProperties" parçası) scope+ad kimliğiyle
    /// karşılaştırır. SSDT bunları host altında "Extended Properties" klasöründe gösterir.
    /// </summary>
    private static IEnumerable<ChildChange> CompareExtendedProperties(
        ObjectKey key, ObjectSnapshot source, ObjectSnapshot target)
    {
        var src = ExtendedMap(source.PartCanonical.GetValueOrDefault("extendedProperties"));
        var tgt = ExtendedMap(target.PartCanonical.GetValueOrDefault("extendedProperties"));

        foreach (var (id, value) in src)
        {
            var (scope, name) = SplitEpId(id);
            var qualified = $"{key.Schema}.{key.Name} · {scope}";

            if (!tgt.TryGetValue(id, out var existing))
                yield return new ChildChange(ChangeAction.Add, "Extended Properties", "Extended Property",
                    name, qualified, TrimValue(value));
            else if (!string.Equals(existing, value, StringComparison.Ordinal))
                yield return new ChildChange(ChangeAction.Change, "Extended Properties", "Extended Property",
                    name, qualified, $"{TrimValue(existing)} → {TrimValue(value)}");
        }

        foreach (var (id, value) in tgt)
        {
            if (src.ContainsKey(id)) continue;
            var (scope, name) = SplitEpId(id);
            yield return new ChildChange(ChangeAction.Delete, "Extended Properties", "Extended Property",
                name, $"{key.Schema}.{key.Name} · {scope}", TrimValue(value));
        }
    }

    /// <summary>"ep|&lt;scope&gt;|&lt;ad&gt;=&lt;değer&gt;" satırlarını "scope|ad" → değer olarak indeksler.</summary>
    private static Dictionary<string, string> ExtendedMap(string? canonical)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(canonical)) return map;

        foreach (var line in canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('|', 3);
            if (fields.Length < 3) continue;
            var equals = fields[2].IndexOf('=');
            var name = equals >= 0 ? fields[2][..equals] : fields[2];
            var value = equals >= 0 ? fields[2][(equals + 1)..] : string.Empty;
            map[$"{fields[1]}|{name}"] = value;
        }
        return map;
    }

    private static (string Scope, string Name) SplitEpId(string id)
    {
        var bar = id.IndexOf('|');
        return bar >= 0 ? (id[..bar], id[(bar + 1)..]) : (string.Empty, id);
    }

    private static string TrimValue(string value) =>
        value.Length > 80 ? value[..77] + "..." : value;

    private static string DescribeTemporalChange(string? source, string? target)
    {
        var on = !string.IsNullOrEmpty(source);
        var was = !string.IsNullOrEmpty(target);
        if (on && !was) return "SYSTEM_VERSIONING açılıyor";
        if (!on && was) return "SYSTEM_VERSIONING kapatılıyor";
        return "history/PERIOD ayarı değişiyor";
    }

    /// <summary>
    /// İzinler ya var ya yok — "değişim" kavramı yoktur, Add/Delete olarak raporlanır.
    /// SSDT bunları host altında "Permissions" klasöründe gösterir.
    /// </summary>
    private static IEnumerable<ChildChange> ComparePermissions(
        ObjectKey key, ObjectSnapshot source, ObjectSnapshot target)
    {
        var src = PermissionMap(source.PartCanonical.GetValueOrDefault("permissions"));
        var tgt = PermissionMap(target.PartCanonical.GetValueOrDefault("permissions"));
        var baseHost = key.Kind == ObjectKind.Schema ? key.Name : $"{key.Schema}.{key.Name}";

        foreach (var (id, entry) in src)
            if (!tgt.ContainsKey(id))
                yield return new ChildChange(ChangeAction.Add, "Permissions", "Permission",
                    entry.Display, $"{baseHost} · {entry.Scope}", null);

        foreach (var (id, entry) in tgt)
            if (!src.ContainsKey(id))
                yield return new ChildChange(ChangeAction.Delete, "Permissions", "Permission",
                    entry.Display, $"{baseHost} · {entry.Scope}", null);
    }

    /// <summary>"perm|&lt;scope&gt;|&lt;ifade&gt;" satırlarını "scope|ifade" kimliğiyle indeksler.</summary>
    private static Dictionary<string, (string Scope, string Display)> PermissionMap(string? canonical)
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(canonical)) return map;

        foreach (var line in canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('|', 3);
            if (fields.Length < 3) continue;
            var scope = fields[1];
            // Kolon izninde kolonu ada yansıt; obje/şema izninde ifade tek başına yeter.
            var display = scope.StartsWith("col:", StringComparison.Ordinal)
                ? $"{fields[2]} ({scope})"
                : fields[2];
            map[$"{scope}|{fields[2]}"] = (scope, display);
        }
        return map;
    }

    private static IEnumerable<ChildChange> CompareMembers(
        ObjectKey key, ObjectSnapshot source, ObjectSnapshot target)
    {
        var src = new HashSet<string>(MemberList(source.PartCanonical.GetValueOrDefault("members")), StringComparer.OrdinalIgnoreCase);
        var tgt = new HashSet<string>(MemberList(target.PartCanonical.GetValueOrDefault("members")), StringComparer.OrdinalIgnoreCase);

        foreach (var member in src)
            if (!tgt.Contains(member))
                yield return new ChildChange(ChangeAction.Add, "Membership", "Member", member, $"{key.Name}\\{member}", null);

        foreach (var member in tgt)
            if (!src.Contains(member))
                yield return new ChildChange(ChangeAction.Delete, "Membership", "Member", member, $"{key.Name}\\{member}", null);
    }

    private static IEnumerable<string> MemberList(string? canonical)
    {
        if (string.IsNullOrEmpty(canonical)) yield break;
        foreach (var line in canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('|', 2);
            if (fields.Length == 2) yield return fields[1];
        }
    }

    /// <summary>Index satırındaki pk/uq bayrakları kategoriyi belirler — SSDT de ayrı klasörler gösterir.</summary>
    private static (string Category, string ItemType) Classify(string part, string line) => part switch
    {
        "checks" => ("Check Constraints", "Check Constraint"),
        "statistics" => ("Statistics", "Statistics"),
        "fullText" => ("Full-Text", "Full-Text Index"),
        "xmlIndexes" => ("XML Indexes", "XML Index"),
        "spatialIndexes" => ("Spatial Indexes", "Spatial Index"),
        "foreignKeys" => ("Foreign Keys", "Foreign Key"),
        "indexes" when line.Contains("|pk=1", StringComparison.Ordinal) => ("Primary Key", "Primary Key"),
        "indexes" when line.Contains("|uq=1", StringComparison.Ordinal) => ("Unique Constraints", "Unique Constraint"),
        "indexes" => ("Indexes", "Index"),
        _ => ("Properties", part),
    };

    /// <summary>Kanonik satırlar "tip|ad|alan=değer|…" biçimindedir; ada göre indeksle.</summary>
    private static Dictionary<string, string> LinesByName(string? canonical)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(canonical)) return result;

        foreach (var line in canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('|');
            if (fields.Length < 2) continue;
            result.TryAdd(fields[1], line);
        }
        return result;
    }

    /// <summary>Hangi alanların değiştiğini söyler — tam metin alt paneldeki diff'te zaten var.</summary>
    private static string? DescribeLineChange(string target, string source)
    {
        var changed = new List<string>();
        var targetFields = FieldMap(target);
        var sourceFields = FieldMap(source);

        foreach (var (key, value) in sourceFields)
            if (!targetFields.TryGetValue(key, out var other) || !string.Equals(other, value, StringComparison.Ordinal))
                changed.Add(key);

        foreach (var (key, _) in targetFields)
            if (!sourceFields.ContainsKey(key)) changed.Add(key);

        return changed.Count == 0 ? null : string.Join(", ", changed.Distinct()) + " değişti";
    }

    private static Dictionary<string, string> FieldMap(string line)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var fields = line.Split('|');
        for (var i = 2; i < fields.Length; i++)
        {
            var equals = fields[i].IndexOf('=');
            if (equals > 0) map[fields[i][..equals]] = fields[i][(equals + 1)..];
            else map[$"alan{i}"] = fields[i];
        }
        return map;
    }

    private static string? Summarize(string line)
    {
        var fields = line.Split('|');
        if (fields.Length < 3) return null;

        var keys = fields.FirstOrDefault(f => f.StartsWith("keys=", StringComparison.Ordinal));
        if (keys is not null && keys.Length > 5) return keys[5..];

        var cols = fields.FirstOrDefault(f => f.StartsWith("cols=", StringComparison.Ordinal));
        return cols is not null ? cols[5..] : fields[2];
    }

    /// <summary>SSDT çıktısıyla aynı okunsun diye tür adları İngilizce.</summary>
    private static string TypeName(ObjectKind kind) => kind switch
    {
        ObjectKind.Schema => "Schema",
        ObjectKind.Table => "Table",
        ObjectKind.View => "View",
        ObjectKind.Procedure => "Procedure",
        ObjectKind.ScalarFunction => "Scalar Function",
        ObjectKind.InlineTableFunction => "Inline Function",
        ObjectKind.TableFunction => "Table Function",
        ObjectKind.Trigger => "Trigger",
        ObjectKind.Synonym => "Synonym",
        ObjectKind.Sequence => "Sequence",
        ObjectKind.Role => "Role",
        ObjectKind.User => "User",
        ObjectKind.UserDefinedType => "User-Defined Type",
        ObjectKind.TableType => "Table Type",
        ObjectKind.PartitionFunction => "Partition Function",
        ObjectKind.PartitionScheme => "Partition Scheme",
        ObjectKind.DdlTrigger => "DDL Trigger",
        ObjectKind.FullTextCatalog => "Full-Text Catalog",
        ObjectKind.Database => "Database",
        _ => kind.ToString(),
    };
}
