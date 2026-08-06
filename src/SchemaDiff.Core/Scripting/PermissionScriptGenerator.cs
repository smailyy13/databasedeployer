using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record PermissionScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<SkippedObject> Skipped)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>
/// Obje ve şema izinleri için dağıtım script'i üretir: <c>GRANT</c> / <c>DENY</c> / <c>REVOKE</c>.
///
/// İzinler host objenin parçasıdır; bu dilim MODÜL ve TABLO dilimlerinden SONRA çalışmalıdır
/// (host obje ve grantee var olmalı). Bir izin ya vardır ya yoktur — "değişim" yoktur:
/// GRANT→DENY dönüşümü, eski iznin REVOKE'u + yeni iznin verilmesi olarak çıkar. Grantee
/// hedefte yoksa GRANT patlar; bu yüzden her ifade <c>IF DATABASE_PRINCIPAL_ID</c> koruması
/// içinde yazılır. Veri kaybı imkânsızdır.
/// </summary>
public static class PermissionScriptGenerator
{
    public static PermissionScriptResult Generate(
        CompareResult result, ISet<ObjectKey>? selection = null, string? generatedAt = null)
    {
        var included = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();
        var body = new StringBuilder(4096);

        foreach (var diff in result.Differences)
        {
            if (selection is not null && !selection.Contains(diff.Key)) continue;
            if (diff.Kind is DiffKind.Removed or DiffKind.Indeterminate) continue;

            var host = diff.Key;
            if (host.Kind is not (ObjectKind.Schema or ObjectKind.Table or ObjectKind.View
                or ObjectKind.Procedure or ObjectKind.ScalarFunction or ObjectKind.InlineTableFunction
                or ObjectKind.TableFunction))
                continue;

            var wanted = PermissionMap(result.Source.Objects.GetValueOrDefault(host));
            var current = PermissionMap(result.Target.Objects.GetValueOrDefault(host));

            var statements = new List<string>();

            // Kaynakta olup hedefte olmayan → ver (GRANT/DENY).
            foreach (var (id, entry) in wanted)
                if (!current.ContainsKey(id))
                    statements.Add(Grant(host, entry));

            // Hedefte olup kaynakta olmayan → geri al (REVOKE).
            foreach (var (id, entry) in current)
                if (!wanted.ContainsKey(id))
                    statements.Add(Revoke(host, entry));

            if (statements.Count == 0) continue;

            body.AppendLine($"PRINT N'İzinler: {Describe(host)}';");
            foreach (var s in statements) body.AppendLine(s);
            body.AppendLine("GO");
            body.AppendLine();
            included.Add(host);
        }

        if (included.Count == 0) return new PermissionScriptResult(string.Empty, included, skipped);

        var sb = new StringBuilder(body.Length + 512);
        sb.AppendLine("/* ---- 5b) İzinler (GRANT/DENY) -------------------------------------------");
        sb.AppendLine("   Modül ve tablolardan SONRA çalışır. Eksik grantee atlanır.");
        sb.AppendLine("   ------------------------------------------------------------------------ */");
        sb.AppendLine();
        sb.Append(body);

        return new PermissionScriptResult(sb.ToString(), included, skipped);
    }

    private static string Grant(ObjectKey host, PermissionEntry e)
    {
        var verb = e.State == "DENY" ? "DENY" : "GRANT";
        var withGrant = e.State == "GRANT_WITH_GRANT_OPTION" ? " WITH GRANT OPTION" : string.Empty;
        return Guarded(e.Grantee,
            $"    {verb} {PermClause(e)} {Target(host)} TO [{e.Grantee}]{withGrant};");
    }

    private static string Revoke(ObjectKey host, PermissionEntry e) => Guarded(e.Grantee,
        $"    REVOKE {PermClause(e)} {Target(host)} FROM [{e.Grantee}];");

    private static string Guarded(string grantee, string statement) =>
        $"IF DATABASE_PRINCIPAL_ID(N'{Escape(grantee)}') IS NOT NULL\n{statement}";

    private static string PermClause(PermissionEntry e) =>
        e.Column is null ? e.Permission : $"{e.Permission} ([{e.Column}])";

    private static string Target(ObjectKey host) => host.Kind == ObjectKind.Schema
        ? $"ON SCHEMA::[{host.Name}]"
        : $"ON OBJECT::[{host.Schema}].[{host.Name}]";

    /// <summary>"permissions" parçasını kimliğe göre indeksler ve alanları ayrıştırır.</summary>
    private static Dictionary<string, PermissionEntry> PermissionMap(ObjectSnapshot? snapshot)
    {
        var map = new Dictionary<string, PermissionEntry>(StringComparer.Ordinal);
        if (snapshot is null || !snapshot.PartCanonical.TryGetValue("permissions", out var canonical))
            return map;

        foreach (var line in canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // perm|<scope>|<state> <permission> TO <grantee>
            var fields = line.Split('|', 3);
            if (fields.Length < 3) continue;
            var entry = Parse(fields[1], fields[2]);
            if (entry is not null) map[$"{fields[1]}|{fields[2]}"] = entry;
        }
        return map;
    }

    private static PermissionEntry? Parse(string scope, string statement)
    {
        var firstSpace = statement.IndexOf(' ');
        if (firstSpace < 0) return null;
        var state = statement[..firstSpace];
        var rest = statement[(firstSpace + 1)..];

        var toIndex = rest.LastIndexOf(" TO ", StringComparison.Ordinal);
        if (toIndex < 0) return null;
        var permission = rest[..toIndex];
        var grantee = rest[(toIndex + 4)..];

        var column = scope.StartsWith("col:", StringComparison.Ordinal) ? scope[4..] : null;
        return new PermissionEntry(state, permission, grantee, column);
    }

    private static string Describe(ObjectKey key) =>
        key.Kind == ObjectKind.Schema ? $"Schema [{key.Name}]" : $"{key.Kind} [{key.Schema}].[{key.Name}]";

    private static string Escape(string value) => value.Replace("'", "''");

    private sealed record PermissionEntry(string State, string Permission, string Grantee, string? Column);
}
