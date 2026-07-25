using System.Globalization;
using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Jobs;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Cli;

internal static class Reports
{
    public static string Ms(TimeSpan value) =>
        $"{value.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture),8} ms";

    private static string Num(long? value) =>
        value?.ToString("N0", CultureInfo.CurrentCulture) ?? "?";

    public static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"=== {title} ".PadRight(92, '='));
    }

    public static void Extraction(DatabaseSnapshot snapshot)
    {
        var report = snapshot.Report;
        foreach (var (name, elapsed) in report.QueryTimings.OrderByDescending(t => t.Value))
        {
            var rows = report.RowCounts.TryGetValue(name, out var count) ? $"{count,9:N0} satır" : "";
            Console.WriteLine($"  {name,-20} {Ms(elapsed)}  {rows}");
        }
        Console.WriteLine($"  {"-- çekim (paralel)",-20} {Ms(report.TotalExtraction)}");
        Console.WriteLine($"  {"-- normalize+hash",-20} {Ms(report.Normalization)}");
    }

    public static void Warnings(DatabaseSnapshot snapshot, string indent = "  ")
    {
        foreach (var warning in snapshot.Report.Warnings)
            Console.WriteLine($"{indent}[!] {snapshot.Database}: {warning}");
    }

    public static void Summary(CompareResult result)
    {
        Console.WriteLine($"  Kaynak obje        : {result.Source.Count:N0}");
        Console.WriteLine($"  Hedef obje         : {result.Target.Count:N0}");
        Console.WriteLine($"  Aynı               : {result.EqualCount:N0}");
        Console.WriteLine($"  Eklenecek (Added)  : {result.AddedCount:N0}");
        Console.WriteLine($"  Silinecek (Removed): {result.RemovedCount:N0}");
        Console.WriteLine($"  Değişmiş (Changed) : {result.ChangedCount:N0}");
        if (result.IndeterminateCount > 0)
            Console.WriteLine($"  Belirsiz           : {result.IndeterminateCount:N0}  <-- karşılaştırılamadı");
        Console.WriteLine();
        Console.WriteLine($"  Karşılaştırma süresi: {Ms(result.CompareTime)}");
    }

    private static string Marker(DiffKind kind) => kind switch
    {
        DiffKind.Added => "+",
        DiffKind.Removed => "-",
        DiffKind.Changed => "~",
        DiffKind.Indeterminate => "?",
        _ => " ",
    };

    public static void Differences(CompareResult result, int limit, string title)
    {
        Header($"{title} (ilk {Math.Min(limit, result.Differences.Count)} / {result.Differences.Count})");
        foreach (var diff in result.Differences.Take(limit))
        {
            var parts = diff.ChangedParts.Count > 0 ? $"   [{string.Join(", ", diff.ChangedParts)}]" : "";
            Console.WriteLine($"  {Marker(diff.Kind)} {diff.Key}{parts}");
        }
        if (result.Differences.Count > limit)
            Console.WriteLine($"  ... {result.Differences.Count - limit} fark daha (--details ile artırın)");
    }

    public static void ObjectDetail(CompareResult result, string name)
    {
        var match = result.Source.Objects.Keys
            .Concat(result.Target.Objects.Keys)
            .FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)
                              || string.Equals($"{k.Schema}.{k.Name}", name, StringComparison.OrdinalIgnoreCase));

        if (match == default)
        {
            Console.WriteLine($"\n  '{name}' iki tarafta da bulunamadı.");
            return;
        }

        Header($"DETAY  {match}");
        result.Source.Objects.TryGetValue(match, out var source);
        result.Target.Objects.TryGetValue(match, out var target);

        Console.WriteLine("--- KAYNAK ---");
        Console.WriteLine(source?.Canonical ?? "(yok)");
        Console.WriteLine();
        Console.WriteLine("--- HEDEF ---");
        Console.WriteLine(target?.Canonical ?? "(yok)");
    }

    /// <summary>
    /// Kapsam sondası: bu veritabanında neyi karşılaştırıyoruz, neyi kaçırıyoruz.
    /// Kapsanmayan ama sayısı sıfırdan büyük olan her satır bir yapılacak iştir.
    /// </summary>
    public static void Coverage(CoverageReport report)
    {
        Header($"KAPSAM SONDASI  {report.Server} / {report.Database}  (SQL {report.ProductVersion})");

        WriteSection("KARŞILAŞTIRMAYA GİRENLER", report.Items.Where(i => i.Covered));
        WriteSection("KARŞILAŞTIRMAYA GİRMEYENLER", report.Items.Where(i => !i.Covered));

        Console.WriteLine();
        Console.WriteLine($"  Sonda süresi: {Ms(report.Elapsed)}");

        var gaps = report.Gaps;
        Header("KAPSAM EKSİĞİ — bu veritabanında var, karşılaştırmaya girmiyor");

        if (gaps.Count == 0)
        {
            Console.WriteLine("  Yok. Bu veritabanındaki tüm obje sınıfları karşılaştırma kapsamında.");
            return;
        }

        foreach (var gap in gaps)
            Console.WriteLine($"    ✗ {gap.Item,-38} {Num(gap.Count),12}");

        Console.WriteLine();
        Console.WriteLine($"  {gaps.Count} sınıf eksik. Bunlar karşılaştırmada SESSİZCE atlanıyor —");
        Console.WriteLine("  yani rapor temiz görünse bile bu objelerdeki farklar görünmez.");
        Console.WriteLine("  Faz 1'in kapsamı bu liste olmalı.");

        var unreadable = report.Items.FirstOrDefault(i => i.Item.StartsWith("Şifrelenmiş", StringComparison.Ordinal));
        if (unreadable?.Count > 0)
            Console.WriteLine($"\n  [!] {Num(unreadable.Count)} modülün tanımı okunamıyor " +
                              "(şifrelenmiş ya da VIEW DEFINITION yetkisi yok).");
    }

    /// <summary>Üretilen script'in ne kapsadığını ve neyi KAPSAMADIĞINI açıkça yazar.</summary>
    public static void ScriptSummary(string job, string path, ScriptResult script)
    {
        Header($"SCRIPT ÜRETİLDİ — {job}");
        Console.WriteLine($"  Dosya: {path}");
        Console.WriteLine();
        Console.WriteLine($"  Script'e giren  : {script.Included.Count,5} obje (modül + şema)");
        Console.WriteLine($"  KAPSAM DIŞI     : {script.OutOfScope.Count,5} obje (tablo/kolon/index — uygulanmayacak)");
        if (script.Skipped.Count > 0)
            Console.WriteLine($"  Atlanan         : {script.Skipped.Count,5} obje (aşağıda sebebiyle)");

        foreach (var skip in script.Skipped)
            Console.WriteLine($"      ! {skip.Key}  →  {skip.Reason}");

        if (script.HadDependencyCycle)
            Console.WriteLine("\n  [!] Yeni objeler arasında döngüsel referans var; sıralama tam garanti edilemedi.");

        if (script.OutOfScope.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Kapsam dışı kalan değişiklikler bu script ÇALIŞTIRILDIKTAN SONRA da duracak.");
            Console.WriteLine("  Onlar için SSDT kullanın ya da tablo dilimi tamamlanana kadar bekleyin.");
        }
    }

    /// <summary>Modül + tablo + rol dilimlerini birlikte özetler; veri kaybı adımları ayrıca uyarılır.</summary>
    public static void CombinedScriptSummary(
        string job, string path, ScriptResult? module, TableScriptResult? table,
        RoleScriptResult? role = null, TypeScriptResult? type = null)
    {
        Header($"SCRIPT ÜRETİLDİ — {job}");
        Console.WriteLine($"  Dosya: {path}");
        Console.WriteLine();

        var moduleIncluded = module?.Included.Count ?? 0;
        var tableIncluded = table?.Included.Count ?? 0;
        var roleIncluded = role?.Included.Count ?? 0;
        var typeIncluded = type?.Included.Count ?? 0;
        Console.WriteLine($"  Script'e giren  : {moduleIncluded + tableIncluded + roleIncluded + typeIncluded,5} obje " +
                          $"(tip {typeIncluded} + tablo {tableIncluded} + modül {moduleIncluded} + rol {roleIncluded})");

        // Modül üreteci tablolar başka dilimde ele alınıyorsa onları zaten kapsam dışı saymıyor.
        var outOfScope = module?.OutOfScope.Count ?? 0;
        if (outOfScope > 0)
            Console.WriteLine($"  KAPSAM DIŞI     : {outOfScope,5} obje (sequence/synonym vb. — uygulanmayacak)");

        var skipped = (module?.Skipped ?? []).Concat(table?.Skipped ?? [])
            .Concat(role?.Skipped ?? []).Concat(type?.Skipped ?? []).ToList();
        if (skipped.Count > 0)
        {
            Console.WriteLine($"  Atlanan         : {skipped.Count,5} obje (aşağıda sebebiyle)");
            foreach (var skip in skipped)
                Console.WriteLine($"      ! {skip.Key}  →  {skip.Reason}");
        }

        var dataLoss = table?.DataLossActions ?? [];
        if (dataLoss.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  [!] {dataLoss.Count} VERİ KAYBI adımı script'e ALINMADI (--allow-data-loss ile eklenir):");
            foreach (var action in dataLoss)
                Console.WriteLine($"      ✗ {action.Table} · {action.Column}  →  {action.Description}");
        }

        if ((module?.HadDependencyCycle ?? false) || (table?.HadDependencyCycle ?? false))
            Console.WriteLine("\n  [!] Yeni objeler arasında döngüsel referans var; sıralama tam garanti edilemedi.");
    }

    private static void WriteSection(string title, IEnumerable<CoverageItem> items)
    {
        Console.WriteLine($"\n  {title}");
        foreach (var group in items.GroupBy(i => i.Category))
        {
            Console.WriteLine($"    {group.Key}");
            foreach (var item in group)
            {
                var mark = item.Covered ? "✓" : (item.Count > 0 ? "✗" : "·");
                var count = item.Error is not null ? "okunamadı" : Num(item.Count);
                Console.WriteLine($"      {mark} {item.Item,-38} {count,12}" +
                                  (item.Error is not null ? $"   ({item.Error})" : ""));
            }
        }
    }

    // --- İş listesi ve seçim ---

    public static void JobList(IReadOnlyList<ComparisonJob> jobs)
    {
        var width = Math.Max(4, jobs.Max(j => j.Name.Length));
        for (var i = 0; i < jobs.Count; i++)
        {
            var job = jobs[i];
            Console.WriteLine($"  {i + 1,3}) {job.Name.PadRight(width)}   {job.SourceLabel}  →  {job.TargetLabel}");
        }
    }

    // --- Akan sonuçlar ---

    /// <summary>Bir iş biter bitmez yazılır; diğerlerinin bitmesi beklenmez.</summary>
    public static void JobCompleted(JobResult result, int completed, int total, int detailLimit)
    {
        var progress = $"[{completed}/{total}]";

        if (result.Comparison is not { } comparison)
        {
            Console.WriteLine($"{progress} ✗ {result.Name,-18} {Ms(result.Duration)}   HATA: {result.Error}");
            return;
        }

        Console.WriteLine(
            $"{progress} ✓ {result.Name,-18} {Ms(result.Duration)}   " +
            $"obje {comparison.Source.Count:N0} | aynı {comparison.EqualCount:N0} | " +
            $"+{comparison.AddedCount} -{comparison.RemovedCount} ~{comparison.ChangedCount}" +
            (comparison.IndeterminateCount > 0 ? $" ?{comparison.IndeterminateCount}" : ""));

        Warnings(comparison.Source, "          ");
        Warnings(comparison.Target, "          ");

        if (detailLimit <= 0) return;

        foreach (var diff in comparison.Differences.Take(detailLimit))
        {
            var parts = diff.ChangedParts.Count > 0 ? $"   [{string.Join(", ", diff.ChangedParts)}]" : "";
            Console.WriteLine($"          {Marker(diff.Kind)} {diff.Key}{parts}");
        }
        if (comparison.Differences.Count > detailLimit)
            Console.WriteLine($"          ... {comparison.Differences.Count - detailLimit} fark daha");
    }

    public static void JobSummary(IReadOnlyList<JobResult> results)
    {
        Header("ÖZET");
        Console.WriteLine($"  {"İş",-18}{"Obje",10}{"Aynı",10}{"Ekle",7}{"Sil",7}{"Değişen",9}{"Süre",12}");
        Console.WriteLine($"  {new string('-', 73)}");

        int totalObjects = 0, totalEqual = 0, totalAdded = 0, totalRemoved = 0, totalChanged = 0;

        foreach (var result in results)
        {
            if (result.Comparison is not { } comparison)
            {
                Console.WriteLine($"  {result.Name,-18}  HATA: {result.Error}");
                continue;
            }

            Console.WriteLine(
                $"  {result.Name,-18}{comparison.Source.Count,10:N0}{comparison.EqualCount,10:N0}" +
                $"{comparison.AddedCount,7:N0}{comparison.RemovedCount,7:N0}{comparison.ChangedCount,9:N0}" +
                $"{result.Duration.TotalMilliseconds,9:N0} ms");

            totalObjects += comparison.Source.Count;
            totalEqual += comparison.EqualCount;
            totalAdded += comparison.AddedCount;
            totalRemoved += comparison.RemovedCount;
            totalChanged += comparison.ChangedCount;
        }

        Console.WriteLine($"  {new string('-', 73)}");
        Console.WriteLine(
            $"  {"TOPLAM",-18}{totalObjects,10:N0}{totalEqual,10:N0}" +
            $"{totalAdded,7:N0}{totalRemoved,7:N0}{totalChanged,9:N0}");

        var failed = results.Count(r => !r.Succeeded);
        if (failed > 0)
            Console.WriteLine($"\n  [!] {failed} iş tamamlanamadı — yukarıdaki toplamlar eksiktir.");
    }

    /// <summary>
    /// Değişen tabloların deployment açısından sınıflandırılması: hangisi dolu tabloda
    /// bloklanır, hangisi veri kaybettirir, hangisi sorunsuz uygulanır.
    /// </summary>
    public static void RiskReport(IReadOnlyList<(string Job, TableRisk Risk)> risks, int limit)
    {
        Header("DEPLOYMENT RİSK ANALİZİ");

        if (risks.Count == 0)
        {
            Console.WriteLine("  Tablo yapısında risk taşıyan değişiklik yok.");
            return;
        }

        var blocking = risks.Where(r => r.Risk.WillBlock).ToList();
        var losesData = blocking.Where(r => r.Risk.Risk == DeploymentRisk.DataLoss).ToList();
        var emptyButRisky = risks
            .Where(r => r.Risk.TargetRowCount == 0 && r.Risk.Risk >= DeploymentRisk.BlockedIfNotEmpty)
            .ToList();
        var unknownRows = risks
            .Where(r => r.Risk.TargetRowCount is null && r.Risk.Risk >= DeploymentRisk.BlockedIfNotEmpty)
            .ToList();

        foreach (var (job, risk) in risks.Take(limit))
        {
            // Boş tabloda "veri kaybı" demek yanıltıcı: kaybedecek veri yok.
            var label = risk switch
            {
                { WillBlock: true, Risk: DeploymentRisk.DataLoss } => "VERİ KAYBI",
                { WillBlock: true } => "BLOKLANIR",
                { TargetRowCount: 0, Risk: >= DeploymentRisk.BlockedIfNotEmpty } => "boş tablo",
                { TargetRowCount: null, Risk: >= DeploymentRisk.BlockedIfNotEmpty } => "RİSKLİ (satır sayısı okunamadı)",
                { Risk: DeploymentRisk.InPlace } => "yerinde",
                _ => "güvenli",
            };

            Console.WriteLine();
            Console.WriteLine($"  [{label}]  {job}.{risk.Table.Schema}.{risk.Table.Name}" +
                              $"   ({Num(risk.TargetRowCount)} satır)");
            foreach (var finding in risk.Findings.OrderByDescending(f => f.Risk))
                Console.WriteLine($"      {finding.Column,-24} {finding.Description}");
        }

        if (risks.Count > limit)
            Console.WriteLine($"\n  ... {risks.Count - limit} tablo daha (--details ile artırın)");

        Console.WriteLine();
        Console.WriteLine($"  ÖZET: {risks.Count} tabloda yapısal değişiklik var.");
        Console.WriteLine($"        {blocking.Count} tanesi hedefte DOLU → deployment sırasında duracak" +
                          $" ({losesData.Count} tanesinde veri kaybı riski).");
        Console.WriteLine($"        {emptyButRisky.Count} tanesi riskli ama hedefte boş → sorunsuz uygulanır.");
        var lowRisk = risks.Count(r => r.Risk.Risk < DeploymentRisk.BlockedIfNotEmpty);
        if (lowRisk > 0)
            Console.WriteLine($"        {lowRisk} tanesi düşük riskli → yerinde uygulanır.");
        if (unknownRows.Count > 0)
            Console.WriteLine($"        {unknownRows.Count} tanesinin satır sayısı okunamadı → elle kontrol edin.");
        if (blocking.Count > 0)
            Console.WriteLine("        Bloklanacaklar için deployment öncesi plan gerekir (boşaltma ya da veri taşıma script'i).");
    }

    /// <summary>Olası yeniden adlandırmalar: drop+create yerine sp_rename önerisi (tabloda veri kaybını önler).</summary>
    public static void RenameReport(string job, IReadOnlyList<RenameCandidate> renames)
    {
        Header($"OLASI YENİDEN ADLANDIRMA — {job}");
        Console.WriteLine("  Bu çiftler drop+create olarak görünüyor ama muhtemelen rename. TABLODA drop+create");
        Console.WriteLine("  VERİ KAYBETTİRİR — doğrusu sp_rename'dir. Script'e otomatik konmaz; siz karar verin.\n");
        foreach (var r in renames)
            Console.WriteLine($"  {r.Removed}  →  {r.Added}   ({r.Basis})");
        Console.WriteLine();
        Console.WriteLine("  Örnek:  EXEC sp_rename '[şema].[eskiAd]', 'yeniAd';");
    }

    /// <summary>Hedef ortamdaki trigger durumları; iki ortam arasında farklı olanlar işaretlenir.</summary>
    public static void TriggerReport(IReadOnlyList<JobResult> results)
    {
        Header("TRIGGER DURUMLARI (hedef ortam)");
        Console.WriteLine($"  {"İş",-18}{"Trigger",-46}{"Durum",-10}");
        Console.WriteLine($"  {new string('-', 74)}");

        var mismatches = new List<string>();
        var found = false;

        foreach (var result in results)
        {
            if (result.Comparison is not { } comparison) continue;

            var triggers = comparison.Target.Objects.Values
                .Where(o => o.Key.Kind == ObjectKind.Trigger)
                .OrderBy(o => o.Key.Schema, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Key.Name, StringComparer.OrdinalIgnoreCase);

            foreach (var trigger in triggers)
            {
                found = true;
                var state = trigger.IsDisabled == true ? "PASİF" : "aktif";
                Console.WriteLine($"  {result.Name,-18}{$"{trigger.Key.Schema}.{trigger.Key.Name}",-46}{state,-10}");

                if (comparison.Source.Objects.TryGetValue(trigger.Key, out var sourceTrigger)
                    && sourceTrigger.IsDisabled != trigger.IsDisabled)
                {
                    mismatches.Add(
                        $"{result.Name}.{trigger.Key.Schema}.{trigger.Key.Name}: " +
                        $"kaynak={(sourceTrigger.IsDisabled == true ? "PASİF" : "aktif")}, hedef={state}");
                }
            }
        }

        if (!found) Console.WriteLine("  (trigger bulunamadı)");

        if (mismatches.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  [!] {mismatches.Count} trigger'ın durumu iki ortamda farklı:");
            foreach (var mismatch in mismatches) Console.WriteLine($"      {mismatch}");
        }
    }
}
