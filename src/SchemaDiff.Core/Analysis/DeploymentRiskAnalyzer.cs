using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Analysis;

public enum DeploymentRisk
{
    /// <summary>Ekleme türü değişiklik; mevcut veriye dokunmaz.</summary>
    Safe = 0,

    /// <summary>ALTER COLUMN ile yerinde uygulanır, veri korunur.</summary>
    InPlace = 1,

    /// <summary>Tablo doluysa deployment bloklanır (SSDT "possible data loss" der).</summary>
    BlockedIfNotEmpty = 2,

    /// <summary>Veri kaybı kesin: kolon siliniyor ya da tip daraltılıyor.</summary>
    DataLoss = 3,
}

public sealed record RiskFinding(string Column, DeploymentRisk Risk, string Description);

public sealed record TableRisk(
    ObjectKey Table,
    DeploymentRisk Risk,
    long? TargetRowCount,
    IReadOnlyList<RiskFinding> Findings)
{
    /// <summary>
    /// Deployment sırasında gerçekten duracak olanlar: riskli değişiklik VE hedefte veri var.
    /// Ekibin şu an deploy anında öğrendiği bilgi.
    /// </summary>
    public bool WillBlock => Risk >= DeploymentRisk.BlockedIfNotEmpty && TargetRowCount is > 0;
}

/// <summary>
/// Değişen tabloları "bu deployment'ta ne olacak?" açısından sınıflandırır.
///
/// Kuveyt Türk deployment prosedürü (adım 3.2) şunu söylüyor:
/// "canlı ortamda veri olan bir tabloda değişiklik yapmasına sistemin izin vermiyor...
///  değişiklik yapılacak bir tabloda mutlaka ama mutlaka veri olmamalıdır."
/// Bu analiz o bilgiyi deployment ANINDA değil, ÖNCESİNDE verir.
///
/// Kural: emin olunmayan her durum daha riskli tarafa yuvarlanır. Yanlış "güvenli"
/// demek, yanlış "riskli" demekten çok daha pahalıdır.
/// </summary>
public static class DeploymentRiskAnalyzer
{
    public static IReadOnlyList<TableRisk> Analyze(CompareResult result)
    {
        var risks = new List<TableRisk>();

        foreach (var diff in result.Differences)
        {
            if (diff.Key.Kind != ObjectKind.Table) continue;

            switch (diff.Kind)
            {
                case DiffKind.Removed:
                {
                    // Hedefte var, kaynakta yok → tablo silinecek.
                    var target = result.Target.Objects[diff.Key];
                    risks.Add(new TableRisk(diff.Key, DeploymentRisk.DataLoss, target.RowCount,
                        [new RiskFinding("(tablo)", DeploymentRisk.DataLoss, "Tablo siliniyor")]));
                    break;
                }

                case DiffKind.Changed:
                {
                    var source = result.Source.Objects[diff.Key];
                    var target = result.Target.Objects[diff.Key];
                    var findings = CompareColumns(source.Columns, target.Columns);

                    // Değişmiş ama kolon seviyesinde risk bulunmayan tablolar da rapora girer.
                    // Sessizce düşmeleri, "risk raporunda yoktu" diye güven yaratıp yanıltır.
                    if (findings.Count == 0)
                    {
                        var parts = string.Join(", ", diff.ChangedParts);
                        findings.Add(new RiskFinding("(yapı)", DeploymentRisk.Safe,
                            diff.ChangedParts.Contains("indexes")
                                ? $"Değişen bölümler: {parts} — veri korunur, büyük tabloda süre alabilir"
                                : $"Değişen bölümler: {parts} — kolon yapısında risk bulunmadı"));
                    }

                    var overall = findings.Max(f => f.Risk);
                    risks.Add(new TableRisk(diff.Key, overall, target.RowCount, findings));
                    break;
                }
            }
        }

        // Önce gerçekten duracaklar, sonra dolu/bilinmeyen tablolar, sonra boşlar.
        return risks
            .OrderByDescending(r => r.WillBlock)
            .ThenByDescending(r => r.TargetRowCount is null or > 0)
            .ThenByDescending(r => r.Risk)
            .ThenByDescending(r => r.TargetRowCount ?? 0)
            .ToList();
    }

    private static List<RiskFinding> CompareColumns(
        IReadOnlyList<ColumnInfo>? source, IReadOnlyList<ColumnInfo>? target)
    {
        var findings = new List<RiskFinding>();
        if (source is null || target is null) return findings;

        var targetByName = target.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var sourceByName = source.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var column in source)
        {
            if (targetByName.TryGetValue(column.Name, out var existing))
            {
                findings.AddRange(CompareColumn(column, existing));
                continue;
            }

            // Kaynakta var, hedefte yok → kolon eklenecek.
            if (column.IsComputed || column.IsNullable || column.DefaultDefinition is not null)
            {
                findings.Add(new RiskFinding(column.Name, DeploymentRisk.Safe,
                    $"Kolon ekleniyor ({column.TypeDisplay})"));
            }
            else
            {
                findings.Add(new RiskFinding(column.Name, DeploymentRisk.BlockedIfNotEmpty,
                    $"NOT NULL kolon ekleniyor ({column.TypeDisplay}) ve DEFAULT tanımı yok — dolu tabloda başarısız olur"));
            }
        }

        foreach (var column in target)
        {
            if (!sourceByName.ContainsKey(column.Name))
                findings.Add(new RiskFinding(column.Name, DeploymentRisk.DataLoss,
                    $"Kolon siliniyor ({column.TypeDisplay}) — içindeki veri kaybolur"));
        }

        return findings;
    }

    /// <param name="source">Olması gereken hâli.</param>
    /// <param name="target">Şu anki prod hâli.</param>
    private static IEnumerable<RiskFinding> CompareColumn(ColumnInfo source, ColumnInfo target)
    {
        if (source.IsIdentity != target.IsIdentity)
        {
            yield return new RiskFinding(source.Name, DeploymentRisk.DataLoss,
                "IDENTITY özelliği değişiyor — ALTER COLUMN ile yapılamaz, tablo yeniden oluşturulmalı");
        }

        if (source.IsComputed != target.IsComputed)
        {
            yield return new RiskFinding(source.Name, DeploymentRisk.DataLoss,
                source.IsComputed
                    ? "Normal kolon computed kolona dönüşüyor — mevcut değerler kaybolur"
                    : "Computed kolon normal kolona dönüşüyor — tablo yeniden oluşturulmalı");
        }

        if (!source.IsNullable && target.IsNullable)
        {
            yield return new RiskFinding(source.Name, DeploymentRisk.BlockedIfNotEmpty,
                "NULL kabul eden kolon NOT NULL yapılıyor — mevcut satırlarda NULL varsa başarısız olur");
        }

        if (!string.Equals(source.Collation, target.Collation, StringComparison.OrdinalIgnoreCase))
        {
            yield return new RiskFinding(source.Name, DeploymentRisk.InPlace,
                $"Collation değişiyor ({target.Collation ?? "-"} → {source.Collation ?? "-"}) — " +
                "kolon index'liyse index önce düşürülmeli");
        }

        var typeChange = ClassifyTypeChange(source, target);
        if (typeChange is not null) yield return typeChange;
    }

    private static RiskFinding? ClassifyTypeChange(ColumnInfo source, ColumnInfo target)
    {
        var sameType = string.Equals(source.TypeName, target.TypeName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(source.TypeSchema, target.TypeSchema, StringComparison.OrdinalIgnoreCase);

        if (sameType)
        {
            // MAX'a genişletme güvenli; MAX'tan sabit uzunluğa daraltma değil.
            if (source.MaxLength == -1 && target.MaxLength != -1)
                return new RiskFinding(source.Name, DeploymentRisk.InPlace,
                    $"{target.TypeDisplay} → {source.TypeDisplay} (genişletme)");

            if (target.MaxLength == -1 && source.MaxLength != -1)
                return new RiskFinding(source.Name, DeploymentRisk.DataLoss,
                    $"{target.TypeDisplay} → {source.TypeDisplay} (MAX'tan daraltma — veri kırpılır)");

            if (source.MaxLength != target.MaxLength)
                return source.MaxLength > target.MaxLength
                    ? new RiskFinding(source.Name, DeploymentRisk.InPlace,
                        $"{target.TypeDisplay} → {source.TypeDisplay} (genişletme)")
                    : new RiskFinding(source.Name, DeploymentRisk.DataLoss,
                        $"{target.TypeDisplay} → {source.TypeDisplay} (daraltma — sığmayan veri kırpılır)");

            if (source.Precision != target.Precision || source.Scale != target.Scale)
                return source.Precision >= target.Precision && source.Scale >= target.Scale
                    ? new RiskFinding(source.Name, DeploymentRisk.InPlace,
                        $"{target.TypeDisplay} → {source.TypeDisplay} (genişletme)")
                    : new RiskFinding(source.Name, DeploymentRisk.DataLoss,
                        $"{target.TypeDisplay} → {source.TypeDisplay} (hassasiyet düşüyor — yuvarlama/taşma)");

            return null;
        }

        // Farklı tip: yalnızca bilinen güvenli genişletmeler InPlace sayılır, gerisi riskli.
        if (IsKnownWidening(target.TypeName, source.TypeName))
            return new RiskFinding(source.Name, DeploymentRisk.InPlace,
                $"{target.TypeDisplay} → {source.TypeDisplay} (güvenli tip genişletmesi)");

        return new RiskFinding(source.Name, DeploymentRisk.DataLoss,
            $"{target.TypeDisplay} → {source.TypeDisplay} (tip değişiyor — dönüşüm kayıplı olabilir)");
    }

    // Sıralı aileler: aynı ailede daha yüksek sıraya geçmek veri kaybetmez.
    private static readonly Dictionary<string, string[]> WideningLadders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["integer"] = ["tinyint", "smallint", "int", "bigint"],
        ["money"] = ["smallmoney", "money"],
        ["float"] = ["real", "float"],
        ["datetime"] = ["smalldatetime", "datetime", "datetime2"],
        ["ansiString"] = ["char", "varchar"],
        ["unicodeString"] = ["nchar", "nvarchar"],
        ["binary"] = ["binary", "varbinary"],
    };

    private static bool IsKnownWidening(string from, string to)
    {
        foreach (var ladder in WideningLadders.Values)
        {
            var fromIndex = Array.FindIndex(ladder, t => t.Equals(from, StringComparison.OrdinalIgnoreCase));
            var toIndex = Array.FindIndex(ladder, t => t.Equals(to, StringComparison.OrdinalIgnoreCase));
            if (fromIndex >= 0 && toIndex >= 0) return toIndex >= fromIndex;
        }

        // varchar → nvarchar: Unicode'a geçiş veri kaybetmez.
        if (from.Equals("varchar", StringComparison.OrdinalIgnoreCase) &&
            to.Equals("nvarchar", StringComparison.OrdinalIgnoreCase)) return true;
        if (from.Equals("char", StringComparison.OrdinalIgnoreCase) &&
            to.Equals("nchar", StringComparison.OrdinalIgnoreCase)) return true;

        return false;
    }
}
