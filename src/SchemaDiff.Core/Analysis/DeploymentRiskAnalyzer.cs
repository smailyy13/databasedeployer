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

/// <param name="Conditional">
/// Bloklama kesin değil, veriye bağlı: constraint (UNIQUE/CHECK/FK) eklemek ya da bir kolonu
/// NOT NULL yapmak, ancak mevcut veri kuralı ihlal ediyorsa başarısız olur — sağlıyorsa geçer.
/// DEFAULT'suz NOT NULL kolon eklemek ya da veri kaybı (drop/daraltma) ise KESİN'dir.
/// </param>
public sealed record RiskFinding(string Column, DeploymentRisk Risk, string Description, bool Conditional = false);

public sealed record TableRisk(
    ObjectKey Table,
    DeploymentRisk Risk,
    long? TargetRowCount,
    IReadOnlyList<RiskFinding> Findings)
{
    /// <summary>
    /// Deployment sırasında gerçekten duracak olanlar: riskli değişiklik VE hedefte veri var.
    /// Satır sayısı bilinmiyorsa (null) da riskli sayılır — "emin değilsek riskli tarafa yuvarla"
    /// ilkesi gereği yanlış "güvenli" demektense yanlış "riskli" demek tercih edilir.
    /// </summary>
    public bool WillBlock => Risk >= DeploymentRisk.BlockedIfNotEmpty && TargetRowCount is null or > 0;

    /// <summary>
    /// Bloklama YALNIZCA koşullu bulgulardan mı kaynaklanıyor? Öyleyse mevcut veri kuralı
    /// sağlıyorsa deployment aslında geçebilir; UI kırmızı "BLOKLANACAK" yerine amber
    /// "KONTROL ET" gösterebilir. Kesin bir blok/veri kaybı varsa bu false olur.
    /// </summary>
    public bool ConditionalOnly => WillBlock &&
        Findings.Where(f => f.Risk >= DeploymentRisk.BlockedIfNotEmpty).All(f => f.Conditional);
}

/// <summary>
/// Değişen tabloları "bu deployment'ta ne olacak?" açısından sınıflandırır.
///
/// Kuveyt Türk deployment prosedürü (adım 3.2) şunu söylüyor:
/// "canlı ortamda veri olan bir tabloda değişiklik yapmasına sistemin izin vermiyor...
///  değişiklik yapılacak bir tabloda mutlaka ama mutlaka veri olmamalıdır."
/// Bu analiz o bilgiyi deployment ANINDA değil, ÖNCESİNDE verir.
///
/// Kapsam: kolon ekleme/silme/tip/null/identity/computed/collation/default değişimi,
/// kolon SIRASI değişimi, ve kolon dışı ama dolu tabloda patlayan constraint eklemeleri
/// (UNIQUE/PRIMARY KEY, CHECK, FOREIGN KEY). Büyük tablolarda yerinde ALTER'ın süre/kilit
/// maliyeti de ayrıca işaretlenir.
///
/// Kural: emin olunmayan her durum daha riskli tarafa yuvarlanır. Yanlış "güvenli"
/// demek, yanlış "riskli" demekten çok daha pahalıdır.
/// </summary>
public static class DeploymentRiskAnalyzer
{
    /// <summary>
    /// Bu satır sayısının üstünde, veri kaybetmeyen bir ALTER bile tabloyu uzun süre
    /// kilitler ve log'u büyütür — ayrı bir performans uyarısı hak eder.
    /// </summary>
    private const long HeavyRewriteRows = 1_000_000;

    public static IReadOnlyList<TableRisk> Analyze(CompareResult result)
    {
        var risks = new List<TableRisk>();

        // Kullanıcının bilerek yok saydığı farkları (kolon sırası / collation) risk olarak
        // geri gündeme getirmeyelim. İki taraftan biri bile "yok say" diyorsa yok sayarız.
        var ignoreOrder = result.Source.IgnoredColumnOrder || result.Target.IgnoredColumnOrder;
        var ignoreCollation = result.Source.IgnoredCollation || result.Target.IgnoredCollation;

        foreach (var diff in result.Differences)
        {
            if (diff.Key.Kind != ObjectKind.Table) continue;

            switch (diff.Kind)
            {
                case DiffKind.Added:
                {
                    // Kaynakta var, hedefte yok → tablo CREATE TABLE ile oluşturulacak.
                    // Hedefte veri olmadığı için bloklamaz; yine de "ne olacak?" raporunda görünsün.
                    var source = result.Source.Objects[diff.Key];
                    var colCount = source.Columns?.Count ?? 0;
                    risks.Add(new TableRisk(diff.Key, DeploymentRisk.Safe, 0,
                        [new RiskFinding("(tablo)", DeploymentRisk.Safe,
                            $"Yeni tablo oluşturuluyor ({colCount} kolon) — hedefte yok, risk yok")]));
                    break;
                }

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
                    var findings = CompareColumns(source.Columns, target.Columns, ignoreOrder, ignoreCollation);

                    // Kolon dışı ama dolu tabloda başarısız olabilen değişiklikler:
                    // UNIQUE/PK (tekrar eden değer), CHECK ve FOREIGN KEY (ihlal eden mevcut veri).
                    findings.AddRange(CompareConstraints(source, target));

                    // Büyük tabloda yerinde (veri korunan) ALTER bile uzun kilit + log büyümesi demek.
                    if (target.RowCount is long rows && rows >= HeavyRewriteRows &&
                        findings.Any(f => f.Risk == DeploymentRisk.InPlace))
                    {
                        findings.Add(new RiskFinding("(performans)", DeploymentRisk.InPlace,
                            $"~{rows:N0} satır: ALTER işlemi tabloyu kilitler, uzun sürebilir ve log'u büyütür"));
                    }

                    // Değişmiş ama hiçbir risk bulunmayan tablolar da rapora girer.
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
        IReadOnlyList<ColumnInfo>? source, IReadOnlyList<ColumnInfo>? target,
        bool ignoreOrder, bool ignoreCollation)
    {
        var findings = new List<RiskFinding>();
        if (source is null || target is null) return findings;

        var targetByName = target.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var sourceByName = source.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var column in source)
        {
            if (targetByName.TryGetValue(column.Name, out var existing))
            {
                findings.AddRange(CompareColumn(column, existing, ignoreCollation));
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

        // Kolon SIRASI değişimi: ortak kolonların göreli sırası farklıysa. Araç düz ALTER
        // üretir, sırayı değiştirmez; SSDT ise tabloyu yeniden oluştururdu. Yani sessizce
        // uygulanmadan kalır — kullanıcıya söylenmeli. Ama kullanıcı "kolon sırasını yok say"
        // dediyse bunu risk olarak geri getirmeyiz.
        if (!ignoreOrder)
        {
            var commonInSource = source.Where(c => targetByName.ContainsKey(c.Name))
                .Select(c => c.Name).ToList();
            var commonInTarget = target.Where(c => sourceByName.ContainsKey(c.Name))
                .Select(c => c.Name).ToList();
            if (!commonInSource.SequenceEqual(commonInTarget, StringComparer.OrdinalIgnoreCase))
            {
                findings.Add(new RiskFinding("(kolon sırası)", DeploymentRisk.InPlace,
                    "Kolon sırası değişiyor — araç ALTER üretir, sıra hedefteki gibi kalır " +
                    "(SSDT bunu tabloyu yeniden oluşturarak yapardı; bu araç veri taşıma üretmez)"));
            }
        }

        return findings;
    }

    /// <param name="source">Olması gereken hâli.</param>
    /// <param name="target">Şu anki prod hâli.</param>
    private static IEnumerable<RiskFinding> CompareColumn(ColumnInfo source, ColumnInfo target, bool ignoreCollation)
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
            // Koşullu: yalnızca mevcut satırlarda NULL varsa başarısız olur (veri taraması yapmıyoruz).
            yield return new RiskFinding(source.Name, DeploymentRisk.BlockedIfNotEmpty,
                "NULL kabul eden kolon NOT NULL yapılıyor — mevcut satırlarda NULL varsa başarısız olur",
                Conditional: true);
        }

        if (!ignoreCollation &&
            !string.Equals(source.Collation, target.Collation, StringComparison.OrdinalIgnoreCase))
        {
            yield return new RiskFinding(source.Name, DeploymentRisk.InPlace,
                $"Collation değişiyor ({target.Collation ?? "-"} → {source.Collation ?? "-"}) — " +
                "kolon index'liyse index önce düşürülmeli");
        }

        // DEFAULT değişimi mevcut satırları etkilemez, yalnızca sonradan eklenen satırları.
        if (!string.Equals(source.DefaultDefinition, target.DefaultDefinition, StringComparison.Ordinal))
        {
            yield return new RiskFinding(source.Name, DeploymentRisk.Safe,
                $"DEFAULT değişiyor ({target.DefaultDefinition ?? "yok"} → {source.DefaultDefinition ?? "yok"}) — " +
                "yalnızca yeni satırları etkiler, mevcut veri değişmez");
        }

        var typeChange = ClassifyTypeChange(source, target);
        if (typeChange is not null) yield return typeChange;
    }

    // --- kolon dışı constraint'ler: dolu tabloda eklenmesi başarısız olabilir ---

    /// <summary>
    /// Kaynakta olup hedefte olmayan (yani eklenecek) constraint'leri bulur. UNIQUE/PK, CHECK
    /// ve FOREIGN KEY eklemek, mevcut veri kuralı sağlamıyorsa dolu tabloda başarısız olur.
    /// Eşleştirme ADA göre değil YAPISAL imzaya göre yapılır — sistem-adlı constraint'lerde
    /// ad iki veritabanında farklı olabileceği için ad karşılaştırması yanlış "eklendi" üretir.
    /// </summary>
    private static IEnumerable<RiskFinding> CompareConstraints(ObjectSnapshot source, ObjectSnapshot target)
    {
        // UNIQUE / PRIMARY KEY / diğer index'ler
        var targetIndexes = SignatureSet(target, "indexes", IndexSignature);
        var sourceIndexes = SignatureSet(source, "indexes", IndexSignature);
        foreach (var line in Lines(source, "indexes"))
        {
            if (targetIndexes.Contains(IndexSignature(line))) continue;

            var f = Fields(line);
            var name = f.GetValueOrDefault("_name", "(index)");
            var keys = f.GetValueOrDefault("keys", "");
            var isPk = f.GetValueOrDefault("pk") == "1";
            var isUq = f.GetValueOrDefault("uq") == "1";
            var isUnique = f.GetValueOrDefault("unique") == "1";

            if (isPk || isUq || isUnique)
            {
                var kind = isPk ? "PRIMARY KEY" : isUq ? "UNIQUE constraint" : "UNIQUE index";
                // Koşullu: yalnızca tabloda tekrar eden değer varsa başarısız olur.
                yield return new RiskFinding(name, DeploymentRisk.BlockedIfNotEmpty,
                    $"{kind} ekleniyor ({keys}) — dolu tabloda tekrar eden değer varsa başarısız olur",
                    Conditional: true);
            }
            else
            {
                yield return new RiskFinding(name, DeploymentRisk.InPlace,
                    $"Index ekleniyor ({keys}) — veri korunur ama büyük tabloda süre alır ve tabloyu kilitler");
            }
        }

        // Kaldırılan index/constraint'ler: dolu tabloda güvenli (yapı gider, veri kalır).
        foreach (var line in Lines(target, "indexes"))
        {
            if (sourceIndexes.Contains(IndexSignature(line))) continue;
            var f = Fields(line);
            var isConstraint = f.GetValueOrDefault("pk") == "1" || f.GetValueOrDefault("uq") == "1";
            yield return new RiskFinding(f.GetValueOrDefault("_name", "(index)"), DeploymentRisk.Safe,
                $"{(isConstraint ? "Constraint" : "Index")} kaldırılıyor ({f.GetValueOrDefault("keys", "")}) — veri korunur");
        }

        // CHECK constraint'leri
        var targetChecks = SignatureSet(target, "checks", CheckSignature);
        var sourceChecks = SignatureSet(source, "checks", CheckSignature);
        foreach (var line in Lines(source, "checks"))
        {
            if (targetChecks.Contains(CheckSignature(line))) continue;

            var f = Fields(line);
            yield return new RiskFinding(f.GetValueOrDefault("_name", "(check)"), DeploymentRisk.BlockedIfNotEmpty,
                $"CHECK ekleniyor ({Shorten(CheckSignature(line))}) — mevcut satırlar kuralı ihlal ediyorsa başarısız olur",
                Conditional: true);
        }

        foreach (var line in Lines(target, "checks"))
        {
            if (sourceChecks.Contains(CheckSignature(line))) continue;
            var f = Fields(line);
            yield return new RiskFinding(f.GetValueOrDefault("_name", "(check)"), DeploymentRisk.Safe,
                "CHECK kaldırılıyor — veri korunur");
        }

        // FOREIGN KEY'ler
        var targetFks = SignatureSet(target, "foreignKeys", ForeignKeySignature);
        var sourceFks = SignatureSet(source, "foreignKeys", ForeignKeySignature);
        foreach (var line in Lines(source, "foreignKeys"))
        {
            if (targetFks.Contains(ForeignKeySignature(line))) continue;

            var f = Fields(line);
            yield return new RiskFinding(f.GetValueOrDefault("_name", "(fk)"), DeploymentRisk.BlockedIfNotEmpty,
                $"FOREIGN KEY ekleniyor ({f.GetValueOrDefault("cols", "")} → {f.GetValueOrDefault("ref", "")}) — " +
                "eşleşmeyen değer taşıyan satır varsa başarısız olur",
                Conditional: true);
        }

        foreach (var line in Lines(target, "foreignKeys"))
        {
            if (sourceFks.Contains(ForeignKeySignature(line))) continue;
            var f = Fields(line);
            yield return new RiskFinding(f.GetValueOrDefault("_name", "(fk)"), DeploymentRisk.Safe,
                "FOREIGN KEY kaldırılıyor — veri korunur");
        }
    }

    private static HashSet<string> SignatureSet(ObjectSnapshot obj, string part, Func<string, string> signature)
        => Lines(obj, part).Select(signature).ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> Lines(ObjectSnapshot obj, string part)
    {
        var canonical = obj.PartCanonical.GetValueOrDefault(part);
        if (string.IsNullOrEmpty(canonical)) return [];
        return canonical.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    // Fiziksel opsiyonlar (fill/padded) ya da ad değişse bile aynı sayılsın diye imza yalnızca
    // benzersizlik türü + anahtar kolonlar + filtreden oluşur.
    private static string IndexSignature(string line)
    {
        var f = Fields(line);
        return $"pk={f.GetValueOrDefault("pk")}|uq={f.GetValueOrDefault("uq")}|" +
               $"unique={f.GetValueOrDefault("unique")}|keys={f.GetValueOrDefault("keys")}|" +
               $"cols={f.GetValueOrDefault("cols")}|filter={f.GetValueOrDefault("filter")}";
    }

    private static string ForeignKeySignature(string line)
    {
        var f = Fields(line);
        return $"ref={f.GetValueOrDefault("ref")}|cols={f.GetValueOrDefault("cols")}";
    }

    // chk|<ad>|<tanım>|disabled=..|notTrusted=.. — tanım '=' ve olası '|' içerebildiği için
    // generic ayrıştırıcıya güvenmeden ad ile disabled bayrağı arasını kesip alıyoruz.
    private static string CheckSignature(string line)
    {
        var firstBar = line.IndexOf('|');
        if (firstBar < 0) return line;
        var secondBar = line.IndexOf('|', firstBar + 1);
        if (secondBar < 0) return line;

        var disabled = line.IndexOf("|disabled=", secondBar + 1, StringComparison.Ordinal);
        return disabled >= 0 ? line[(secondBar + 1)..disabled] : line[(secondBar + 1)..];
    }

    /// <summary>Kanonik satırı alanlara böler: [0]=_type, [1]=_name, sonrası key=value.</summary>
    private static Dictionary<string, string> Fields(string line)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var parts = line.Split('|');
        if (parts.Length > 0) map["_type"] = parts[0];
        if (parts.Length > 1) map["_name"] = parts[1];

        for (var i = 2; i < parts.Length; i++)
        {
            var eq = parts[i].IndexOf('=');
            if (eq > 0) map[parts[i][..eq]] = parts[i][(eq + 1)..];
            else map[$"_f{i}"] = parts[i];
        }
        return map;
    }

    private static string Shorten(string value) =>
        value.Length > 60 ? value[..57] + "..." : value;

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
