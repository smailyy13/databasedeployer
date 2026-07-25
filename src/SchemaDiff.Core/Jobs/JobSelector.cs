using System.Globalization;

namespace SchemaDiff.Core.Jobs;

/// <summary>
/// Kullanıcı seçimini iş listesine çevirir. Aynı ifade hem etkileşimli seçimde
/// hem de komut satırı argümanında kullanılır — böylece elle koşulan bir seçim
/// birebir aynı şekilde script'e taşınabilir.
///
/// Kabul edilen biçimler:  "1,3"  "2-5"  "EDWSTG,EDW"  "1,4-6,EDWDM"  "a" / "all" / "*"
/// </summary>
public static class JobSelector
{
    /// <summary>
    /// Görünmez karakterler. Seçim boru hattından ya da yönlendirilmiş dosyadan
    /// geldiğinde başına BOM (U+FEFF) eklenebiliyor; temizlenmezse "1" sayıya çevrilemiyor.
    /// </summary>
    private static readonly char[] Invisible = ['﻿', '​', '‎', '‏'];

    public static IReadOnlyList<ComparisonJob> Select(IReadOnlyList<ComparisonJob> jobs, string? expression)
    {
        if (string.IsNullOrWhiteSpace(expression)) return [];

        expression = string.Concat(expression.Where(c => !Invisible.Contains(c)));
        if (string.IsNullOrWhiteSpace(expression)) return [];

        var trimmed = expression.Trim();
        if (trimmed is "a" or "A" or "*" || trimmed.Equals("all", StringComparison.OrdinalIgnoreCase))
            return jobs;

        var selected = new HashSet<int>();
        var tokens = trimmed.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var token in tokens)
        {
            var dash = token.IndexOf('-');

            // "4-6" aralığı — yalnızca iki taraf da sayıysa. Aksi hâlde ad olabilir
            // ("EDW-STG" gibi), o yüzden ada göre eşleştirmeye düşülür.
            if (dash > 0 && dash < token.Length - 1
                && int.TryParse(token[..dash], NumberStyles.Integer, CultureInfo.InvariantCulture, out var from)
                && int.TryParse(token[(dash + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var to))
            {
                if (from > to) (from, to) = (to, from);
                for (var i = from; i <= to; i++) selected.Add(RequireIndex(jobs, i, token));
                continue;
            }

            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                selected.Add(RequireIndex(jobs, index, token));
                continue;
            }

            var match = jobs
                .Select((job, i) => (job, i))
                .Where(x => string.Equals(x.job.Name, token, StringComparison.OrdinalIgnoreCase))
                .Select(x => x.i)
                .ToList();

            if (match.Count == 0)
                throw new ArgumentException(
                    $"'{token}' ne bir sıra numarası ne de tanımlı bir iş adı. " +
                    $"Geçerli adlar: {string.Join(", ", jobs.Select(j => j.Name))}");

            selected.Add(match[0]);
        }

        // Seçim sırası değil, tanım sırası korunur — rapor sırası öngörülebilir olsun.
        return [.. selected.Order().Select(i => jobs[i])];
    }

    private static int RequireIndex(IReadOnlyList<ComparisonJob> jobs, int oneBased, string token)
    {
        if (oneBased < 1 || oneBased > jobs.Count)
            throw new ArgumentException(
                $"'{token}' geçersiz: sıra numarası 1 ile {jobs.Count} arasında olmalı.");

        return oneBased - 1;
    }
}
