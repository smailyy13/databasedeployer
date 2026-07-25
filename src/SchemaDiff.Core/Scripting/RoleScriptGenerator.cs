using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record RoleScriptOptions
{
    /// <summary>Hedefte olup kaynakta olmayan rolleri DROP et (üyeleri önce düşürülür).</summary>
    public bool IncludeDrops { get; init; } = true;

    public bool WrapInTransaction { get; init; } = true;

    public string? GeneratedAt { get; init; }

    public static readonly RoleScriptOptions Default = new();
}

public sealed record RoleScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<SkippedObject> Skipped)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>
/// Kullanıcı tanımlı roller ve üyelikleri için dağıtım script'i üretir:
/// <c>CREATE ROLE</c> / <c>DROP ROLE</c> / <c>ALTER ROLE ADD|DROP MEMBER</c>.
///
/// Veri kaybı imkânsızdır. Üyeler ad üzerinden eklenir — üye (kullanıcı ya da başka rol)
/// hedefte yoksa <c>ALTER ROLE ADD MEMBER</c> patlar; bu yüzden her üyelik kendi
/// <c>IF DATABASE_PRINCIPAL_ID</c> koruması içinde yazılır, eksik üye sessizce atlanır.
/// Rolün owner'ı bilinçli olarak kapsam dışı (genelde dbo; ortamlar arası fark gürültüdür).
/// </summary>
public static class RoleScriptGenerator
{
    public static RoleScriptResult Generate(
        CompareResult result, ISet<ObjectKey>? selection = null, RoleScriptOptions? options = null)
    {
        options ??= RoleScriptOptions.Default;

        var included = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();

        var adds = new List<ObjectKey>();
        var drops = new List<ObjectKey>();
        var changes = new List<ObjectKey>();

        foreach (var diff in result.Differences)
        {
            if (diff.Key.Kind != ObjectKind.Role) continue;
            if (selection is not null && !selection.Contains(diff.Key)) continue;

            switch (diff.Kind)
            {
                case DiffKind.Added: adds.Add(diff.Key); break;
                case DiffKind.Removed: drops.Add(diff.Key); break;
                case DiffKind.Changed: changes.Add(diff.Key); break;
            }
        }

        if (adds.Count == 0 && drops.Count == 0 && changes.Count == 0)
            return new RoleScriptResult(string.Empty, included, skipped);

        var sb = new StringBuilder(4096);
        WriteHeader(sb, result, options);

        if (options.WrapInTransaction)
        {
            sb.AppendLine("SET XACT_ABORT ON;");
            sb.AppendLine("GO");
            sb.AppendLine("BEGIN TRANSACTION;");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        foreach (var key in adds.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"PRINT N'Rol oluşturuluyor: {Escape(key.Name)}';");
            sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{Escape(key.Name)}') IS NULL");
            sb.AppendLine($"    CREATE ROLE [{key.Name}];");
            foreach (var member in Members(result.Source, key))
                WriteAddMember(sb, key.Name, member);
            sb.AppendLine("GO");
            sb.AppendLine();
            included.Add(key);
        }

        foreach (var key in changes.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            var wanted = Members(result.Source, key);
            var current = Members(result.Target, key);

            var toAdd = wanted.Where(m => !current.Contains(m)).ToList();
            var toDrop = current.Where(m => !wanted.Contains(m)).ToList();
            if (toAdd.Count == 0 && toDrop.Count == 0)
            {
                // Yalnızca owner değişmiş olabilir; onu üretmiyoruz.
                skipped.Add(new SkippedObject(key, "üyelik farkı yok (owner değişimi üretilmiyor)"));
                continue;
            }

            sb.AppendLine($"PRINT N'Rol üyeliği güncelleniyor: {Escape(key.Name)}';");
            foreach (var member in toDrop) WriteDropMember(sb, key.Name, member);
            foreach (var member in toAdd) WriteAddMember(sb, key.Name, member);
            sb.AppendLine("GO");
            sb.AppendLine();
            included.Add(key);
        }

        if (options.IncludeDrops)
        {
            foreach (var key in drops.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"PRINT N'Rol siliniyor: {Escape(key.Name)}';");
                sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{Escape(key.Name)}') IS NOT NULL");
                sb.AppendLine("BEGIN");
                // Üyeleri önce düşür — üyesi olan rol silinemez.
                foreach (var member in Members(result.Target, key))
                    sb.AppendLine($"    ALTER ROLE [{key.Name}] DROP MEMBER [{member}];");
                sb.AppendLine($"    DROP ROLE [{key.Name}];");
                sb.AppendLine("END");
                sb.AppendLine("GO");
                sb.AppendLine();
                included.Add(key);
            }
        }
        else
        {
            foreach (var key in drops)
                skipped.Add(new SkippedObject(key, "DROP kapalı — rol hedefte kalacak"));
        }

        if (options.WrapInTransaction)
        {
            sb.AppendLine("COMMIT TRANSACTION;");
            sb.AppendLine("GO");
        }

        return new RoleScriptResult(sb.ToString(), included, skipped);
    }

    private static void WriteAddMember(StringBuilder sb, string role, string member)
    {
        sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{Escape(member)}') IS NOT NULL");
        sb.AppendLine($"    ALTER ROLE [{role}] ADD MEMBER [{member}];");
    }

    private static void WriteDropMember(StringBuilder sb, string role, string member)
    {
        sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{Escape(member)}') IS NOT NULL");
        sb.AppendLine($"    ALTER ROLE [{role}] DROP MEMBER [{member}];");
    }

    /// <summary>Rolün "members" parçasından üye adlarını çıkarır ("member|&lt;ad&gt;").</summary>
    private static HashSet<string> Members(DatabaseSnapshot snapshot, ObjectKey key)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!snapshot.Objects.TryGetValue(key, out var obj)) return set;
        if (!obj.PartCanonical.TryGetValue("members", out var canonical)) return set;

        foreach (var line in canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('|', 2);
            if (fields.Length == 2) set.Add(fields[1]);
        }
        return set;
    }

    private static void WriteHeader(StringBuilder sb, CompareResult result, RoleScriptOptions options)
    {
        sb.AppendLine("/*");
        sb.AppendLine("    SchemaDiff — rol ve üyelik dağıtım script'i");
        sb.AppendLine($"    Kaynak : {result.Source.Server} / {result.Source.Database}");
        sb.AppendLine($"    Hedef  : {result.Target.Server} / {result.Target.Database}");
        if (options.GeneratedAt is not null) sb.AppendLine($"    Üretim : {options.GeneratedAt}");
        sb.AppendLine("    Veri kaybı riski yoktur. Eksik üyeler (hedefte olmayan principal) atlanır.");
        sb.AppendLine("*/");
        sb.AppendLine();
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
