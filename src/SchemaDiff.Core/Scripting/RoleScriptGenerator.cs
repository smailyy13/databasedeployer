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
        var userAdds = new List<ObjectKey>();
        var userDrops = new List<ObjectKey>();
        var userChanges = new List<ObjectKey>();

        foreach (var diff in result.Differences)
        {
            if (selection is not null && !selection.Contains(diff.Key)) continue;

            if (diff.Key.Kind == ObjectKind.User)
            {
                switch (diff.Kind)
                {
                    case DiffKind.Added: userAdds.Add(diff.Key); break;
                    case DiffKind.Removed: userDrops.Add(diff.Key); break;
                    case DiffKind.Changed: userChanges.Add(diff.Key); break;
                }
                continue;
            }
            if (diff.Key.Kind != ObjectKind.Role) continue;

            switch (diff.Kind)
            {
                case DiffKind.Added: adds.Add(diff.Key); break;
                case DiffKind.Removed: drops.Add(diff.Key); break;
                case DiffKind.Changed: changes.Add(diff.Key); break;
            }
        }

        if (adds.Count == 0 && drops.Count == 0 && changes.Count == 0
            && userAdds.Count == 0 && userDrops.Count == 0 && userChanges.Count == 0)
            return new RoleScriptResult(string.Empty, included, skipped);

        var sb = new StringBuilder(4096);

        if (options.WrapInTransaction)
        {
            sb.AppendLine("SET XACT_ABORT ON;");
            sb.AppendLine("GO");
            sb.AppendLine("BEGIN TRANSACTION;");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        // Kullanıcılar ÖNCE oluşturulur (rol üyeliğine eklenmeden var olmalılar).
        foreach (var key in userAdds.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (type, schema) = UserDef(result.Source, key);
            var login = type == "S" ? " WITHOUT LOGIN" : string.Empty;
            var sch = !schema.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? $" WITH DEFAULT_SCHEMA=[{schema}]" : string.Empty;
            // Ad zaten varsa VEYA login'in SID'i başka bir kullanıcıya eşliyse (ör. hedef,
            // aynı login'e ait kişisel kopya → login = dbo) atla. Aksi hâlde Msg 15063:
            // "The login already has an account under a different user name".
            sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{Escape(key.Name)}') IS NULL");
            sb.AppendLine($"   AND NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE sid = SUSER_SID(N'{Escape(key.Name)}'))");
            sb.AppendLine($"    CREATE USER [{key.Name}]{login}{sch};");
            sb.AppendLine("GO");
            sb.AppendLine();
            included.Add(key);
        }

        foreach (var key in userChanges.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            var (_, schema) = UserDef(result.Source, key);
            sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{Escape(key.Name)}') IS NOT NULL");
            sb.AppendLine($"    ALTER USER [{key.Name}] WITH DEFAULT_SCHEMA=[{schema}];");
            sb.AppendLine("GO");
            sb.AppendLine();
            included.Add(key);
        }

        foreach (var key in adds.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
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

        // Kullanıcılar EN SON silinir (önce rol üyeliklerinden çıkarıldılar).
        if (options.IncludeDrops)
        {
            foreach (var key in userDrops.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"IF DATABASE_PRINCIPAL_ID(N'{Escape(key.Name)}') IS NOT NULL");
                sb.AppendLine("BEGIN");
                // Kullanıcının sahip olduğu şemaları önce dbo'ya devret; aksi hâlde
                // "owns a schema and cannot be dropped" (Msg 15138) ile patlar. SSDT de böyle yapar.
                sb.AppendLine("    DECLARE @reassign nvarchar(max) = N'';");
                sb.AppendLine("    SELECT @reassign += N'ALTER AUTHORIZATION ON SCHEMA::' + QUOTENAME(s.name) + N' TO [dbo];'");
                sb.AppendLine("    FROM sys.schemas AS s");
                sb.AppendLine($"    WHERE s.principal_id = DATABASE_PRINCIPAL_ID(N'{Escape(key.Name)}');");
                sb.AppendLine("    IF @reassign <> N'' EXEC sys.sp_executesql @reassign;");
                sb.AppendLine($"    DROP USER [{key.Name}];");
                sb.AppendLine("END");
                sb.AppendLine("GO");
                sb.AppendLine();
                included.Add(key);
            }
        }
        else
        {
            foreach (var key in userDrops)
                skipped.Add(new SkippedObject(key, "DROP kapalı — kullanıcı hedefte kalacak"));
        }

        if (options.WrapInTransaction)
        {
            // XACT_ABORT ON: bir adım patlarsa transaction geri sarılmış olur; koşulsuz
            // COMMIT o durumda Msg 3902 verir. @@TRANCOUNT koruması gerçek hatayı bırakır.
            sb.AppendLine("IF @@TRANCOUNT > 0 COMMIT TRANSACTION;");
            sb.AppendLine("GO");
        }

        return new RoleScriptResult(sb.ToString(), included, skipped);
    }

    /// <summary>Kullanıcının "definition" parçasından tip ve default schema'yı okur.</summary>
    private static (string Type, string Schema) UserDef(DatabaseSnapshot snapshot, ObjectKey key)
    {
        string type = "S", schema = "dbo";
        if (snapshot.Objects.TryGetValue(key, out var obj)
            && obj.PartCanonical.TryGetValue("definition", out var canonical))
            foreach (var field in canonical.Split('|'))
                if (field.StartsWith("type=", StringComparison.Ordinal)) type = field[5..];
                else if (field.StartsWith("schema=", StringComparison.Ordinal)) schema = field[7..];
        return (type, schema);
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

    private static string Escape(string value) => value.Replace("'", "''");
}
