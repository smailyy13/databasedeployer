using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record ExtendedPropertyScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<SkippedObject> Skipped)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>
/// Extended property'ler (MS_Description vb.) için dağıtım script'i üretir:
/// <c>sp_addextendedproperty</c> / <c>sp_updateextendedproperty</c> / <c>sp_dropextendedproperty</c>.
///
/// EP'ler host objenin parçasıdır; bu yüzden bu dilim MODÜL ve TABLO dilimlerinden SONRA
/// çalışmalıdır — eklenen bir tablonun/prosedürün EP'si ancak obje oluştuktan sonra uygulanabilir.
/// Silinen objelerin EP'leri objeyle birlikte gider, üretilmez. Veri kaybı imkânsızdır.
/// </summary>
public static class ExtendedPropertyScriptGenerator
{
    // Birleşik script'in oluşturduğu host sınıfları — yalnızca bunların EKLENEN örneklerine
    // EP yazılır (obje gerçekten var olacak). Değişen objelerde host zaten hedefte vardır.
    private static readonly HashSet<ObjectKind> CreatableHosts =
    [
        ObjectKind.Schema, ObjectKind.Table, ObjectKind.View, ObjectKind.Procedure,
        ObjectKind.ScalarFunction, ObjectKind.InlineTableFunction, ObjectKind.TableFunction,
        ObjectKind.Trigger,
    ];

    public static ExtendedPropertyScriptResult Generate(
        CompareResult result, ISet<ObjectKey>? selection = null, string? generatedAt = null)
    {
        var included = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();
        var body = new StringBuilder(4096);

        foreach (var diff in result.Differences)
        {
            if (selection is not null && !selection.Contains(diff.Key)) continue;
            if (diff.Kind == DiffKind.Removed || diff.Kind == DiffKind.Indeterminate) continue;

            var host = diff.Key;
            if (host.Kind is not (ObjectKind.Schema or ObjectKind.Table or ObjectKind.View
                or ObjectKind.Procedure or ObjectKind.ScalarFunction or ObjectKind.InlineTableFunction
                or ObjectKind.TableFunction or ObjectKind.Trigger or ObjectKind.Sequence))
                continue;

            var source = result.Source.Objects.GetValueOrDefault(host);
            var target = result.Target.Objects.GetValueOrDefault(host);

            var wanted = ExtendedMap(source);
            var current = ExtendedMap(target);

            // Eklenen obje: host oluşturulacaksa tüm EP'leri ekle; değilse atla.
            if (diff.Kind == DiffKind.Added && !CreatableHosts.Contains(host.Kind))
            {
                if (wanted.Count > 0)
                    skipped.Add(new SkippedObject(host, "host bu script'te oluşturulmuyor — EP'leri elle ekleyin"));
                continue;
            }

            var statements = new List<string>();
            foreach (var (id, value) in wanted)
            {
                if (!current.TryGetValue(id, out var existing))
                    statements.Add(Call("sp_addextendedproperty", host, id, value));
                else if (!string.Equals(existing, value, StringComparison.Ordinal))
                    statements.Add(Call("sp_updateextendedproperty", host, id, value));
            }
            foreach (var (id, _) in current)
                if (!wanted.ContainsKey(id))
                    statements.Add(Call("sp_dropextendedproperty", host, id, value: null));

            if (statements.Count == 0) continue;

            body.AppendLine($"PRINT N'Extended property: {Describe(host)}';");
            foreach (var s in statements) body.AppendLine(s);
            body.AppendLine("GO");
            body.AppendLine();
            included.Add(host);
        }

        if (included.Count == 0) return new ExtendedPropertyScriptResult(string.Empty, included, skipped);

        var sb = new StringBuilder(body.Length + 512);
        sb.AppendLine("/* ---- 5a) Extended property ----------------------------------------------");
        sb.AppendLine("   Modül ve tablolardan SONRA çalışır (host objeler var olmalı).");
        sb.AppendLine("   ------------------------------------------------------------------------ */");
        sb.AppendLine();
        sb.Append(body);

        return new ExtendedPropertyScriptResult(sb.ToString(), included, skipped);
    }

    /// <summary>sp_(add|update|drop)extendedproperty çağrısını level0/1/2 ile kurar.</summary>
    private static string Call(string proc, ObjectKey host, string id, string? value)
    {
        var (scope, name) = SplitId(id);
        var sb = new StringBuilder(160);
        sb.Append("EXEC sys.").Append(proc).Append(" @name = N'").Append(Escape(name)).Append('\'');
        if (value is not null) sb.Append(", @value = N'").Append(Escape(value)).Append('\'');

        if (host.Kind == ObjectKind.Schema)
        {
            // Şema seviyesi EP: yalnızca level0.
            sb.Append(", @level0type = N'SCHEMA', @level0name = N'").Append(Escape(host.Name)).Append('\'');
        }
        else
        {
            sb.Append(", @level0type = N'SCHEMA', @level0name = N'").Append(Escape(host.Schema)).Append('\'');
            sb.Append(", @level1type = N'").Append(Level1(host.Kind)).Append("', @level1name = N'")
              .Append(Escape(host.Name)).Append('\'');
            if (scope.StartsWith("col:", StringComparison.Ordinal))
                sb.Append(", @level2type = N'COLUMN', @level2name = N'").Append(Escape(scope[4..])).Append('\'');
        }

        sb.Append(';');
        return sb.ToString();
    }

    private static string Level1(ObjectKind kind) => kind switch
    {
        ObjectKind.Table => "TABLE",
        ObjectKind.View => "VIEW",
        ObjectKind.Procedure => "PROCEDURE",
        ObjectKind.Trigger => "TRIGGER",
        ObjectKind.Sequence => "SEQUENCE",
        _ => "FUNCTION",
    };

    /// <summary>"extendedProperties" parçasını "scope|ad" → değer olarak indeksler.</summary>
    private static Dictionary<string, string> ExtendedMap(ObjectSnapshot? snapshot)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (snapshot is null || !snapshot.PartCanonical.TryGetValue("extendedProperties", out var canonical))
            return map;

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

    private static (string Scope, string Name) SplitId(string id)
    {
        var bar = id.IndexOf('|');
        return bar >= 0 ? (id[..bar], id[(bar + 1)..]) : (string.Empty, id);
    }

    private static string Describe(ObjectKey key) =>
        key.Kind == ObjectKind.Schema ? $"Schema [{key.Name}]" : $"{key.Kind} [{key.Schema}].[{key.Name}]";

    private static string Escape(string value) => value.Replace("'", "''");
}
