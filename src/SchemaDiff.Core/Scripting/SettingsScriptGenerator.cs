using System.Globalization;
using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record SettingsScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<SkippedObject> Skipped)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>
/// Modüllerden SONRA çalışan veritabanı seviyesi ayarlar: plan guide'lar ve database scoped
/// configuration.
///
/// Neden sonra: bir plan guide OBJECT kapsamlıysa bir prosedüre bağlıdır — modül henüz
/// yokken <c>sp_create_plan_guide</c> hata verir. Bu yüzden tip/tablo/modül dilimlerinin
/// hiçbirine ait değiller, kendi dilimleri var.
/// </summary>
public static class SettingsScriptGenerator
{
    /// <summary>
    /// Sayısal değer alan ayarlar. Ötekilerde katalog 0/1 tutar ama T-SQL OFF/ON bekler;
    /// ayrımı bilmeden yazmak geçersiz SQL üretir.
    /// </summary>
    private static readonly HashSet<string> NumericSettings = new(StringComparer.OrdinalIgnoreCase)
    {
        "MAXDOP", "MAX_DOP", "QUERY_OPTIMIZER_HOTFIXES_LEVEL", "DW_COMPATIBILITY_LEVEL",
    };

    public static SettingsScriptResult Generate(
        CompareResult result, ISet<ObjectKey>? selection = null, string? generatedAt = null)
    {
        var included = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();
        var body = new StringBuilder(2048);

        AppendScopedConfiguration(result, selection, body, included, skipped);
        AppendPlanGuides(result, selection, body, included, skipped);

        if (included.Count == 0) return new SettingsScriptResult(string.Empty, included, skipped);

        var sb = new StringBuilder(body.Length + 512);
        sb.Append(body);

        return new SettingsScriptResult(sb.ToString(), included, skipped);
    }

    // --- database scoped configuration ---

    private static void AppendScopedConfiguration(
        CompareResult result, ISet<ObjectKey>? selection, StringBuilder body, List<ObjectKey> included, List<SkippedObject> skipped)
    {
        var key = new ObjectKey(string.Empty, "(database)", ObjectKind.Database);
        // Seçim varsa ve (database) objesi seçilmediyse dokunma — kullanıcı yalnız
        // işaretlediği objelerin script'ini ister, DB seviyesi ayar sızmamalı.
        if (selection is not null && !selection.Contains(key)) return;
        var source = SettingsMap(result.Source.Objects.GetValueOrDefault(key));
        var target = SettingsMap(result.Target.Objects.GetValueOrDefault(key));

        var statements = new List<string>();
        foreach (var (name, value) in source)
        {
            if (target.TryGetValue(name, out var current) && current == value) continue;
            if (Render(name, value) is { } statement) statements.Add(statement);
            else skipped.Add(new SkippedObject(key, $"{name} ayarının değeri ({value}) yorumlanamadı — elle uygulayın"));
        }

        if (statements.Count == 0) return;

        foreach (var statement in statements) body.AppendLine(statement);
        body.AppendLine("GO");
        body.AppendLine();
        included.Add(key);
    }

    /// <summary>
    /// Hedefte varsayılana dönmüş ayarlar için üretim YAPILMAZ: katalog yalnızca
    /// varsayılandan sapanları verdiği için "kaynakta yok" bilgisi "varsayılan" demektir,
    /// ve varsayılan değer sürüme göre değişir — tahmin etmektense dokunmuyoruz.
    /// </summary>
    private static string? Render(string name, string value)
    {
        if (NumericSettings.Contains(name))
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
                ? $"ALTER DATABASE SCOPED CONFIGURATION SET {name} = {number.ToString(CultureInfo.InvariantCulture)};"
                : null;

        return value switch
        {
            "0" => $"ALTER DATABASE SCOPED CONFIGURATION SET {name} = OFF;",
            "1" => $"ALTER DATABASE SCOPED CONFIGURATION SET {name} = ON;",
            _ => null,
        };
    }

    /// <summary>"dbconfig|&lt;ad&gt;|value=…|secondary=…" satırlarını ad → değer olarak indeksler.</summary>
    private static Dictionary<string, string> SettingsMap(ObjectSnapshot? snapshot)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (snapshot?.PartCanonical.TryGetValue("scopedConfiguration", out var canonical) != true) return map;

        foreach (var line in canonical!.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = line.Split('|');
            if (fields.Length < 3) continue;
            var value = fields[2].StartsWith("value=", StringComparison.Ordinal) ? fields[2][6..] : fields[2];
            map[fields[1]] = value;
        }
        return map;
    }

    // --- plan guide ---

    private static void AppendPlanGuides(
        CompareResult result, ISet<ObjectKey>? selection,
        StringBuilder body, List<ObjectKey> included, List<SkippedObject> skipped)
    {
        foreach (var diff in result.Differences)
        {
            if (diff.Key.Kind != ObjectKind.PlanGuide) continue;
            if (selection is not null && !selection.Contains(diff.Key)) continue;

            var name = diff.Key.Name.Replace("'", "''");

            if (diff.Kind == DiffKind.Removed)
            {
                body.AppendLine($"EXEC sp_control_plan_guide N'DROP', N'{name}';");
                body.AppendLine("GO");
                body.AppendLine();
                included.Add(diff.Key);
                continue;
            }

            if (!result.Source.Objects.TryGetValue(diff.Key, out var source)
                || string.IsNullOrWhiteSpace(source.DisplayScript))
            {
                skipped.Add(new SkippedObject(diff.Key, "CREATE metni yok — snapshot display script'i gerekli"));
                continue;
            }

            // Plan guide ALTER edilemez: değişen guide önce düşürülür.
            if (diff.Kind == DiffKind.Changed)
                body.AppendLine($"EXEC sp_control_plan_guide N'DROP', N'{name}';");
            body.AppendLine(source.DisplayScript!.TrimEnd());
            body.AppendLine("GO");
            body.AppendLine();
            included.Add(diff.Key);
        }
    }

    private static string Describe(ObjectKey key) => key.ToString().Replace("'", "''");
}
