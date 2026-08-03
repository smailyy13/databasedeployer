using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Analysis;

/// <summary>
/// Üretilen script'in EN BAŞINA konacak "dolu tabloda bloklanacak" uyarı bloğunu kurar.
/// Deploy edilecek her gövde (ileri ve/veya ters) için o karşılaştırmanın hedefinde DOLU
/// olup duracak tablolarını listeler. Deploy'dan önce görülmesi gereken tek özet budur.
///
/// Endpoint'in içine gömülü kalmasın diye ayrı ve saf tutuldu: girdi → metin, test edilebilir.
/// </summary>
public static class DeploymentWarning
{
    /// <summary>
    /// <paramref name="segments"/>: script'e yazılacak her gövde için (karşılaştırma, seçim).
    /// Seçim null ise o gövdedeki tüm tablolar dahildir. Bloklayan tablo yoksa metin boştur.
    /// </summary>
    public static (int Count, string Text) Build(
        IReadOnlyList<(CompareResult Comparison, ISet<ObjectKey>? Selection)> segments)
    {
        var lines = new List<string>();
        var count = 0;

        foreach (var (cmp, sel) in segments)
        {
            var blocking = DeploymentRiskAnalyzer.Analyze(cmp)
                .Where(r => r.WillBlock && (sel is null || sel.Contains(r.Table)));

            foreach (var r in blocking)
            {
                count++;
                var rows = r.TargetRowCount is long n ? $"~{n:N0} satır" : "satır sayısı okunamadı";
                // [BLOKLANACAK] kesin başarısız; [KONTROL ET] yalnızca mevcut veri ihlal ediyorsa.
                var tag = r.ConditionalOnly ? "KONTROL ET  " : "BLOKLANACAK ";
                lines.Add($"   • [{tag}] [{r.Table.Schema}].[{r.Table.Name}]  ({cmp.Target.Database}, {rows})");
                foreach (var f in r.Findings.Where(f => f.Risk >= DeploymentRisk.BlockedIfNotEmpty))
                    lines.Add($"       - {f.Column}: {f.Description}");
            }
        }

        if (count == 0) return (0, string.Empty);

        var sb = new StringBuilder();
        sb.AppendLine("/* ====================================================================");
        sb.AppendLine("   ⚠ DEPLOYMENT UYARISI — DOLU TABLOLAR");
        sb.AppendLine();
        sb.AppendLine($"   Aşağıdaki {count} tabloda, hedefte VERİ VARKEN yapılacak değişiklik sorun");
        sb.AppendLine("   çıkarır: ya bloklanır (ALTER / ADD CONSTRAINT başarısız olur) ya da veri");
        sb.AppendLine("   kaybettirir (kolon/tablo silme, tip daraltma). Kuveyt Türk prosedürü");
        sb.AppendLine("   (3.2): değişecek tabloda MUTLAKA veri olmamalıdır.");
        sb.AppendLine();
        sb.AppendLine("   [BLOKLANACAK] = kesin başarısız.  [KONTROL ET] = yalnızca mevcut veri");
        sb.AppendLine("   kuralı ihlal ediyorsa başarısız (UNIQUE/CHECK/FK ekleme, NULL→NOT NULL).");
        sb.AppendLine();
        foreach (var line in lines) sb.AppendLine(line);
        sb.AppendLine();
        sb.AppendLine("   Çalıştırmadan önce bu tabloları boşaltın ya da elle veri taşıma");
        sb.AppendLine("   planlayın. Ayrıntı: uygulamadaki \"Deployment risk\" sekmesi.");
        sb.AppendLine("   ==================================================================== */");
        sb.AppendLine();
        return (count, sb.ToString());
    }
}
