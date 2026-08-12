using System.Diagnostics;
using System.Text;
using SchemaDiff.Cli;
using SchemaDiff.Core;
using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Jobs;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Normalization;
using SchemaDiff.Core.Scripting;

Console.OutputEncoding = Encoding.UTF8;

var options = CliOptions.Parse(args);
if (options is null)
{
    CliOptions.PrintUsage();
    return 2;
}

try
{
    return await RunAsync(options);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"HATA: {ex.Message}");
    if (options.Verbose) Console.Error.WriteLine(ex);
    return 2;
}

async Task<int> RunAsync(CliOptions opt)
{
    if (opt.InitConfig is not null)
    {
        await JobConfiguration.WriteTemplateAsync(opt.InitConfig);
        Console.WriteLine($"Örnek yapılandırma yazıldı: {opt.InitConfig}");
        Console.WriteLine("Bağlantı bilgilerini düzenleyip --config ile kullanın.");
        return 0;
    }

    if (opt.Coverage)
    {
        if (opt.Source is null)
        {
            Console.Error.WriteLine("HATA: --coverage için --source zorunlu (isteğe bağlı --target ile iki taraf birden).");
            return 2;
        }

        Reports.Coverage(await CoverageProbe.RunAsync(opt.Source));
        if (opt.Target is not null) Reports.Coverage(await CoverageProbe.RunAsync(opt.Target));
        return 0;
    }

    var jobs = await BuildJobsAsync(opt);
    if (jobs is null) return 2;

    if (opt.ListOnly)
    {
        Console.WriteLine($"Tanımlı {jobs.Count} iş:\n");
        Reports.JobList(jobs);
        return 0;
    }

    var selected = ResolveSelection(jobs, opt);
    if (selected is null) return 2;
    if (selected.Count == 0)
    {
        Console.WriteLine("Seçim yapılmadı, çıkılıyor.");
        return 0;
    }

    return await ExecuteAsync(selected, opt);
}

// --- İş listesinin kaynağı ---

async Task<IReadOnlyList<ComparisonJob>?> BuildJobsAsync(CliOptions opt)
{
    if (opt.Config is not null) return await JobConfiguration.LoadAsync(opt.Config);

    if (opt.Source is null || opt.Target is null)
    {
        Console.Error.WriteLine(
            "HATA: İş listesi için ya --config <dosya> ya da --source ile --target birlikte verilmeli.");
        Console.Error.WriteLine("      Örnek yapılandırma üretmek için: --init-config jobs.json");
        return null;
    }

    if (opt.Databases.Count > 0)
        return [.. opt.Databases.Select(db => ComparisonJob.ForDatabase(opt.Source, opt.Target, db))];

    // Tek çift: adı hedef veritabanından türet.
    var single = new ComparisonJob("karşılaştırma", opt.Source, opt.Target);
    return [single with { Name = single.TargetLabel }];
}

// --- Seçim ---

IReadOnlyList<ComparisonJob>? ResolveSelection(IReadOnlyList<ComparisonJob> jobs, CliOptions opt)
{
    try
    {
        if (opt.All) return jobs;
        if (opt.Selection is not null) return JobSelector.Select(jobs, opt.Selection);
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"HATA: {ex.Message}");
        return null;
    }

    // Tek iş varsa sormanın anlamı yok.
    if (jobs.Count == 1) return jobs;

    if (opt.Interactive || !Console.IsInputRedirected) return Prompt(jobs);

    Console.Error.WriteLine("HATA: Hangi işlerin koşacağı belirtilmedi.");
    Console.Error.WriteLine("      Etkileşimli seçim için --select, tümü için --all,");
    Console.Error.WriteLine("      script'ten çağırırken --jobs \"1,3\" ya da --jobs \"AD1,AD2\" kullanın.");
    Console.Error.WriteLine("      Tanımlı işleri görmek için --list.");
    return null;
}

IReadOnlyList<ComparisonJob>? Prompt(IReadOnlyList<ComparisonJob> jobs)
{
    Console.WriteLine("Karşılaştırılacak işleri seçin:\n");
    Reports.JobList(jobs);
    Console.WriteLine();
    Console.Write("Seçim  (1,3  |  2-5  |  ad  |  a = hepsi  |  boş = iptal): ");

    var input = Console.ReadLine();
    try
    {
        var selection = JobSelector.Select(jobs, input);
        if (selection.Count > 0)
            Console.WriteLine($"\nSeçilen: {string.Join(", ", selection.Select(j => j.Name))}");
        return selection;
    }
    catch (ArgumentException ex)
    {
        Console.Error.WriteLine($"HATA: {ex.Message}");
        return null;
    }
}

// --- Koşum ---

async Task<int> ExecuteAsync(IReadOnlyList<ComparisonJob> selected, CliOptions opt)
{
    var snapshotOptions = new SnapshotOptions
    {
        Normalization = new NormalizationOptions(
            IgnoreWhitespace: !opt.RespectWhitespace,
            IgnoreComments: !opt.RespectComments),
        IgnoreSystemNamedConstraints = !opt.KeepSystemNames,
        IgnoreIndexPhysicalOptions = opt.IgnoreIndexPhysicalOptions,
        IgnoreExtendedProperties = opt.IgnoreExtendedProperties,
        IgnorePermissions = opt.IgnorePermissions,
        CaseSensitiveNames = opt.CaseSensitive,
        // Script üretimi modüllerin özgün tanım metnini gerektirir.
        KeepDisplayScripts = opt.ScriptPath is not null,
    };

    Console.WriteLine();
    Console.WriteLine($"{selected.Count} iş paralel koşuyor " +
                      $"(eşzamanlı sorgu sınırı: {opt.MaxQueries}). Biten anında yazılır:\n");

    var total = Stopwatch.StartNew();
    var results = new List<JobResult>(selected.Count);

    // Sonuçlar tamamlanma sırasına göre akar — hepsinin bitmesi beklenmez.
    await foreach (var result in ComparisonRunner.RunAsync(selected, snapshotOptions, opt.MaxQueries))
    {
        results.Add(result);
        Reports.JobCompleted(result, results.Count, selected.Count, opt.Details);
    }

    total.Stop();

    // Rapor sırası tamamlanma sırası değil, seçim sırasıdır.
    var ordered = ComparisonRunner.Order(selected, results);

    Reports.JobSummary(ordered);
    Console.WriteLine($"\n  TOPLAM (uçtan uca): {Reports.Ms(total.Elapsed)}");

    if (opt.Timings || ordered.Count == 1)
    {
        foreach (var result in ordered.Where(r => r.Succeeded))
        {
            Reports.Header($"{result.Name} — KAYNAK  {result.Comparison!.Source.Database}");
            Reports.Extraction(result.Comparison.Source);
            Reports.Header($"{result.Name} — HEDEF   {result.Comparison.Target.Database}");
            Reports.Extraction(result.Comparison.Target);
        }
    }

    if (opt.Risk)
    {
        var risks = ordered
            .Where(r => r.Succeeded)
            .SelectMany(r => DeploymentRiskAnalyzer.Analyze(r.Comparison!).Select(risk => (r.Name, risk)))
            .OrderByDescending(x => x.risk.WillBlock)
            .ThenByDescending(x => x.risk.TargetRowCount is null or > 0)
            .ThenByDescending(x => x.risk.Risk)
            .ThenByDescending(x => x.risk.TargetRowCount ?? 0)
            .ToList();
        Reports.RiskReport(risks, opt.Details > 0 ? opt.Details : 30);
    }

    if (opt.Triggers) Reports.TriggerReport(ordered);

    // Olası yeniden adlandırmalar her zaman uyarılır — tabloda drop+create veri kaybettirir.
    foreach (var result in ordered.Where(r => r.Succeeded))
    {
        var renames = RenameDetector.Detect(result.Comparison!);
        if (renames.Count > 0) Reports.RenameReport(result.Name, renames);
    }

    if (opt.ScriptPath is not null)
    {
        var single = ordered.Count(r => r.Succeeded) == 1;
        var generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        var wantTables = opt.ScriptScope is "all" or "tables";
        var wantModules = opt.ScriptScope is "all" or "modules";

        foreach (var result in ordered.Where(r => r.Succeeded))
        {
            var built = BuildCombinedScript(result.Comparison!, generatedAt, wantTables, wantModules, opt.AllowDataLoss);

            var path = single
                ? opt.ScriptPath
                : Path.Combine(
                    Path.GetDirectoryName(opt.ScriptPath) is { Length: > 0 } dir ? dir : ".",
                    $"{Path.GetFileNameWithoutExtension(opt.ScriptPath)}.{result.Name}{Path.GetExtension(opt.ScriptPath)}");

            await File.WriteAllTextAsync(path, built.Sql, new UTF8Encoding(true));
            Reports.CombinedScriptSummary(result.Name, path, built.Module, built.Table, built.Role, built.Type);

            // Rollback: ters yönde karşılaştırmanın script'i — ileri deploy'u geri alır.
            // (Yapısal geri alma; silinen tablonun verisi geri gelmez.)
            if (opt.RollbackPath is not null)
            {
                var reversed = SchemaComparer.Compare(result.Comparison!.Target, result.Comparison!.Source);
                var rollback = BuildCombinedScript(reversed, generatedAt, wantTables, wantModules, opt.AllowDataLoss);

                var rbPath = single
                    ? opt.RollbackPath
                    : Path.Combine(
                        Path.GetDirectoryName(opt.RollbackPath) is { Length: > 0 } rbDir ? rbDir : ".",
                        $"{Path.GetFileNameWithoutExtension(opt.RollbackPath)}.{result.Name}{Path.GetExtension(opt.RollbackPath)}");

                var header = "/*  ROLLBACK — bu script ileri dağıtımı GERİ ALIR.\n" +
                             "    Yapısal geri alma: yeni objeler düşürülür, silinenler yeniden oluşturulur.\n" +
                             "    UYARI: silinen tabloların/kolonların VERİSİ geri gelmez.  */\n\n";
                await File.WriteAllTextAsync(rbPath, header + rollback.Sql, new UTF8Encoding(true));
                Console.WriteLine($"\n  Rollback script'i yazıldı: {rbPath}");
            }
        }
    }

    if (opt.ShowObject is not null)
    {
        foreach (var result in ordered.Where(r => r.Succeeded))
            Reports.ObjectDetail(result.Comparison!, opt.ShowObject);
    }

    if (ordered.Any(r => !r.Succeeded)) return 2;
    return ordered.Any(r => r.Comparison!.Differences.Count > 0) ? 1 : 0;
}

// Birleşik dağıtım script'ini bir CompareResult'tan üretir. Hem ileri hem rollback için kullanılır.
static (string Sql, ScriptResult? Module, TableScriptResult? Table, RoleScriptResult? Role, TypeScriptResult? Type)
    BuildCombinedScript(CompareResult cmp, string generatedAt, bool wantTables, bool wantModules, bool allowDataLoss)
{
    var sb = new StringBuilder(16384);
    // cmp.Target = bu script'in mutasyona uğrattığı DB (ileri→hedef, rollback→kaynak). USE onu seçer.
    sb.Append(DeploymentHeader.Master(cmp, generatedAt, allowDataLoss, cmp.Target.Database));

    TypeScriptResult? typeScript = null;
    if (wantTables || wantModules)
    {
        typeScript = TypeScriptGenerator.Generate(cmp, selection: null, new TypeScriptOptions { GeneratedAt = generatedAt });
        if (!typeScript.IsEmpty) sb.AppendLine(typeScript.Sql).AppendLine();
    }

    TableScriptResult? tableScript = null;
    if (wantTables)
    {
        tableScript = TableScriptGenerator.Generate(cmp, selection: null, new TableScriptOptions
        {
            GeneratedAt = generatedAt,
            AllowDataLoss = allowDataLoss,
        });
        sb.AppendLine(tableScript.Sql).AppendLine();
    }

    ScriptResult? moduleScript = null;
    RoleScriptResult? roleScript = null;
    if (wantModules)
    {
        moduleScript = ModuleScriptGenerator.Generate(cmp, selection: null, new ScriptOptions
        {
            GeneratedAt = generatedAt,
            TablesHandledElsewhere = wantTables,
            HandledElsewhere = new HashSet<ObjectKind>
            {
                ObjectKind.Role, ObjectKind.User, ObjectKind.UserDefinedType, ObjectKind.TableType,
                ObjectKind.Sequence, ObjectKind.Synonym,
                ObjectKind.PartitionFunction, ObjectKind.PartitionScheme,
            },
        });
        sb.AppendLine(moduleScript.Sql);

        roleScript = RoleScriptGenerator.Generate(cmp, selection: null, new RoleScriptOptions { GeneratedAt = generatedAt });
        if (!roleScript.IsEmpty) sb.AppendLine().AppendLine(roleScript.Sql);

        if (wantTables)
        {
            var epScript = ExtendedPropertyScriptGenerator.Generate(cmp, selection: null, generatedAt);
            if (!epScript.IsEmpty) sb.AppendLine().AppendLine(epScript.Sql);

            var permScript = PermissionScriptGenerator.Generate(cmp, selection: null, generatedAt);
            if (!permScript.IsEmpty) sb.AppendLine().AppendLine(permScript.Sql);
        }
    }

    return (sb.ToString(), moduleScript, tableScript, roleScript, typeScript);
}

internal sealed class CliOptions
{
    public string? Source { get; init; }
    public string? Target { get; init; }
    public string? Config { get; init; }
    public string? InitConfig { get; init; }
    public IReadOnlyList<string> Databases { get; init; } = [];
    public string? Selection { get; init; }
    public bool All { get; init; }
    public bool Interactive { get; init; }
    public bool ListOnly { get; init; }
    public bool Coverage { get; init; }
    public int Details { get; init; } = 30;
    public int MaxQueries { get; init; } = 16;
    public string? ShowObject { get; init; }
    public string? ScriptPath { get; init; }
    public string? RollbackPath { get; init; }
    public string ScriptScope { get; init; } = "all";
    public bool AllowDataLoss { get; init; }
    public bool Risk { get; init; }
    public bool Triggers { get; init; }
    public bool Timings { get; init; }
    public bool CaseSensitive { get; init; }
    public bool KeepSystemNames { get; init; }
    public bool RespectWhitespace { get; init; }
    public bool RespectComments { get; init; }
    public bool IgnoreIndexPhysicalOptions { get; init; }
    public bool IgnoreExtendedProperties { get; init; }
    public bool IgnorePermissions { get; init; }
    public bool Verbose { get; init; }

    public static CliOptions? Parse(string[] args)
    {
        string? source = null, target = null, config = null, initConfig = null;
        string? selection = null, showObject = null, scriptPath = null, rollbackPath = null;
        var scriptScope = "all";
        var allowDataLoss = true;   // ŞİMDİLİK varsayılan açık (SSDT paritesi); --safe ile kapanır
        var databases = new List<string>();
        int details = 30, maxQueries = 16;
        bool all = false, interactive = false, listOnly = false, coverage = false;
        bool risk = false, triggers = false, timings = false;
        bool caseSensitive = false, keepSystemNames = false;
        bool respectWhitespace = false, respectComments = false, ignoreIndexPhysical = false, verbose = false;
        var ignoreExtendedProperties = false;
        var ignorePermissions = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--source" or "-s" when i + 1 < args.Length: source = args[++i]; break;
                case "--target" or "-t" when i + 1 < args.Length: target = args[++i]; break;
                case "--config" or "-c" when i + 1 < args.Length: config = args[++i]; break;
                case "--init-config" when i + 1 < args.Length: initConfig = args[++i]; break;
                case "--databases" when i + 1 < args.Length:
                    databases.AddRange(args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    break;
                case "--jobs" or "-j" when i + 1 < args.Length: selection = args[++i]; break;
                case "--all" or "-a": all = true; break;
                case "--select": interactive = true; break;
                case "--list" or "-l": listOnly = true; break;
                case "--coverage": coverage = true; break;
                case "--details" or "-d" when i + 1 < args.Length: int.TryParse(args[++i], out details); break;
                case "--max-queries" when i + 1 < args.Length: int.TryParse(args[++i], out maxQueries); break;
                case "--show" when i + 1 < args.Length: showObject = args[++i]; break;
                case "--script" when i + 1 < args.Length: scriptPath = args[++i]; break;
                case "--rollback" when i + 1 < args.Length: rollbackPath = args[++i]; break;
                case "--script-scope" when i + 1 < args.Length:
                    scriptScope = args[++i].ToLowerInvariant();
                    if (scriptScope is not ("all" or "modules" or "tables"))
                    {
                        Console.Error.WriteLine("HATA: --script-scope yalnızca all | modules | tables olabilir.");
                        return null;
                    }
                    break;
                case "--allow-data-loss": allowDataLoss = true; break;
                case "--safe": allowDataLoss = false; break;
                case "--risk": risk = true; break;
                case "--triggers": triggers = true; break;
                case "--timings": timings = true; break;
                case "--case-sensitive": caseSensitive = true; break;
                case "--keep-system-names": keepSystemNames = true; break;
                case "--respect-whitespace": respectWhitespace = true; break;
                case "--respect-comments": respectComments = true; break;
                case "--ignore-index-physical": ignoreIndexPhysical = true; break;
                case "--ignore-extended-properties": ignoreExtendedProperties = true; break;
                case "--ignore-permissions": ignorePermissions = true; break;
                case "--verbose" or "-v": verbose = true; break;
                case "--help" or "-h": return null;
                default: Console.Error.WriteLine($"Bilinmeyen argüman: {args[i]}"); return null;
            }
        }

        if (maxQueries < 1) maxQueries = 1;

        return new CliOptions
        {
            Source = source,
            Target = target,
            Config = config,
            InitConfig = initConfig,
            Databases = databases,
            Selection = selection,
            All = all,
            Interactive = interactive,
            ListOnly = listOnly,
            Coverage = coverage,
            Details = details,
            MaxQueries = maxQueries,
            ShowObject = showObject,
            ScriptPath = scriptPath,
            RollbackPath = rollbackPath,
            ScriptScope = scriptScope,
            AllowDataLoss = allowDataLoss,
            Risk = risk,
            Triggers = triggers,
            Timings = timings,
            CaseSensitive = caseSensitive,
            KeepSystemNames = keepSystemNames,
            RespectWhitespace = respectWhitespace,
            RespectComments = respectComments,
            IgnoreIndexPhysicalOptions = ignoreIndexPhysical,
            IgnoreExtendedProperties = ignoreExtendedProperties,
            IgnorePermissions = ignorePermissions,
            Verbose = verbose,
        };
    }

    public static void PrintUsage() => Console.WriteLine("""
        SchemaDiff — SQL Server şema karşılaştırma

        Karşılaştırmanın birimi bir İŞ'tir: isimlendirilmiş bir (kaynak, hedef) çifti.
        İki taraf farklı sunucuda ve farklı veritabanı adında olabilir.
        Seçtiğiniz işler paralel koşar; her biri bittiği anda ekrana yazılır.

        İş listesi nereden gelir:
          --config <dosya>          JSON yapılandırmadan (önerilen)
          -s <conn> -t <conn>       tek bir çift
          -s <conn> -t <conn> --databases A,B,C
                                    iki sunucuda aynı adlı veritabanları için kısayol

        Hangileri koşar:
          -j, --jobs <ifade>        seçim: "1,3" | "2-5" | "AD1,AD2" | "1,4-6,AD"
          -a, --all                 hepsi
              --select              etkileşimli seç (terminalde varsayılan)
          -l, --list                tanımlı işleri listele ve çık

        Raporlar:
          -d, --details <n>         iş başına listelenecek fark sayısı (varsayılan 30, 0 = kapalı)
              --risk                deployment risk analizi: hangi tablo değişikliği dolu
                                    tabloda bloklanır, hangisi veri kaybettirir
              --triggers            hedef ortamdaki trigger aktif/pasif durumu
              --timings             her iş için ayrıntılı sorgu süreleri
              --show <obje>         bir objenin iki taraftaki kanonik metni
              --script <dosya>      dağıtım script'i üret. Varsayılan kapsam: modüller
                                    (view, prosedür, fonksiyon, trigger + şemalar) VE
                                    tablolar (yeni tablo CREATE + kolon ADD/ALTER/DROP).
              --script-scope <k>    script kapsamı: all (varsayılan) | modules | tables
              --rollback <dosya>    ileri dağıtımı GERİ ALAN script'i de üret (ters yön).
                                    Yapısal geri alma — silinen verinin geri gelmediğini unutmayın.
              --safe                veri kaybı riski taşıyan adımları script'ten ÇIKAR (güvenli mod);
                                    ayrı listede raporlanır. VARSAYILAN ŞU AN KAPALI —
                                    yani kolon/tablo silme ve tip daraltma script'e DAHİL edilir.
              --allow-data-loss     varsayılan zaten açık (geriye dönük uyumluluk için tutuluyor)

        Karşılaştırma seçenekleri:
              --case-sensitive         isimleri büyük/küçük harf duyarlı karşılaştır
              --keep-system-names      sistem üretimi constraint adlarını da karşılaştır
              --respect-whitespace     boşluk farklarını fark say
              --respect-comments       yorum farklarını fark say
              --ignore-index-physical  fill factor / padding farklarını yok say
              --ignore-extended-properties  MS_Description gibi açıklamaları karşılaştırma
              --ignore-permissions     rol, üyelik ve izin farklarını karşılaştırma
              --max-queries <n>        eşzamanlı sorgu sınırı (varsayılan 16)

        Tanı:
              --coverage             bu veritabanında hangi obje sınıfları var ve
                                     hangileri karşılaştırma kapsamı DIŞINDA — sayılarıyla.
                                     Kapsam eksiğini tahminle değil veriyle belirler.
                                     Kullanım: --coverage -s "<conn>" [-t "<conn>"]

        Diğer:
              --init-config <dosya>  örnek yapılandırma dosyası üret
          -v, --verbose              hata yığınını yaz

        Çıkış kodu: 0 = fark yok, 1 = fark var, 2 = hata

        Örnekler:
          SchemaDiff.Cli --init-config jobs.json
          SchemaDiff.Cli --config jobs.json --list
          SchemaDiff.Cli --config jobs.json --select --risk
          SchemaDiff.Cli --config jobs.json --jobs "1,3,5-7" --risk --triggers
          SchemaDiff.Cli --config jobs.json --all -d 0
        """);
}
