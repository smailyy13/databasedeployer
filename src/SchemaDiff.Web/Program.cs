using System.Text.Json;
using Microsoft.AspNetCore.Connections;
using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Connections;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;
using SchemaDiff.Web;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<CompareService>();
builder.Services.AddSingleton<RecentConnections>();

var app = builder.Build();

// Kimlik doğrulama YOK ve bağlantı bilgileri sunucuda duruyor. Bu yüzden yalnızca
// yerel makineden erişilebilir olmalı — 0.0.0.0'a açmak bu bağlantılarla sorgu
// koşturma yetkisini ağdaki herkese vermek demektir.
var port = ResolvePort(args);
app.Urls.Clear();
app.Urls.Add($"http://127.0.0.1:{port}");

app.UseDefaultFiles();

// Statik dosyalar her istekte doğrulanmalı. Varsayılan davranışta tarayıcı
// sezgisel önbellekleme yapıp eski CSS/JS'i sunabiliyor; yerel bir araçta bu,
// düzeltilmiş bir hatanın hâlâ duruyormuş gibi görünmesine yol açar.
// "no-cache" içeriği yeniden indirmez, yalnızca ETag ile doğrular.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
        context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate",
});

var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

// --- bağlantılar ---

app.MapGet("/api/connections/recent", (RecentConnections recent) =>
    Results.Ok(recent.All.Select(e => new RecentConnectionDto(
        e.Id, e.Server, e.Database, e.Authentication.ToString(), e.UserName,
        e.Encrypt, e.TrustServerCertificate, e.HasStoredPassword,
        string.IsNullOrWhiteSpace(e.Database) ? e.Server : $"{e.Server}.{e.Database}",
        e.LastUsed))));

app.MapDelete("/api/connections/recent/{id}", (string id, RecentConnections recent) =>
{
    recent.Forget(id);
    return Results.NoContent();
});

app.MapPost("/api/connections/test", async (ConnectionDto dto, RecentConnections recent, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(dto.Server))
        return Results.BadRequest(new { error = "Sunucu adı zorunlu." });

    var probe = await SqlServerExplorer.TestAsync(ToConnectionInfo(dto, recent), ct);
    return Results.Ok(probe);
});

app.MapPost("/api/connections/databases", async (ConnectionDto dto, RecentConnections recent, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(dto.Server))
        return Results.BadRequest(new { error = "Sunucu adı zorunlu." });

    try
    {
        var databases = await SqlServerExplorer.ListDatabasesAsync(ToConnectionInfo(dto, recent), ct);
        return Results.Ok(databases);
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message.Split('\n')[0].Trim() });
    }
});

// --- karşılaştırma ---

app.MapPost("/api/compare", (CompareRequest request, CompareService compare, RecentConnections recent) =>
{
    if (string.IsNullOrWhiteSpace(request.Source.Server) || string.IsNullOrWhiteSpace(request.Target.Server))
        return Results.BadRequest(new { error = "Kaynak ve hedef sunucu zorunlu." });

    if (string.IsNullOrWhiteSpace(request.Source.Database) || string.IsNullOrWhiteSpace(request.Target.Database))
        return Results.BadRequest(new { error = "Kaynak ve hedef veritabanı seçilmeli." });

    var source = ToConnectionInfo(request.Source, recent);
    var target = ToConnectionInfo(request.Target, recent);

    recent.Remember(source, request.Source.RememberPassword);
    recent.Remember(target, request.Target.RememberPassword);

    var session = compare.Start(source, target, request.Options ?? new CompareOptionsDto());
    return Results.Ok(new { runId = session.Id, source = source.Label, target = target.Label });
});

app.MapGet("/api/runs/{id}", (string id, CompareService compare) =>
{
    var session = compare.Get(id);
    if (session is null) return Results.NotFound();
    if (session.Error is not null) return Results.Ok(new { finished = true, error = session.Error });
    return Results.Ok(new { finished = session.Finished, result = session.Dto });
});

app.MapGet("/api/runs/{id}/events", async (string id, HttpContext context, CompareService compare, CancellationToken ct) =>
{
    var session = compare.Get(id);
    if (session is null)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.Headers.ContentType = "text/event-stream";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers["X-Accel-Buffering"] = "no";

    try
    {
        while (!ct.IsCancellationRequested)
        {
            // Sinyali durum okumasından ÖNCE al, yoksa iki adım arasında biten koşum kaçar.
            var changed = session.Changed;

            if (session.Finished)
            {
                if (session.Error is not null)
                    await WriteEventAsync(context.Response, "failed", new { error = session.Error }, jsonOptions, ct);
                else
                    await WriteEventAsync(context.Response, "result", session.Dto!, jsonOptions, ct);
                break;
            }

            await WriteEventAsync(context.Response, "progress",
                new
                {
                    source = session.SourceLabel,
                    target = session.TargetLabel,
                    percent = session.Percent,
                    etaSeconds = session.EtaSeconds,
                    phase = session.Phase,
                }, jsonOptions, ct);

            await changed.WaitAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Tarayıcı sekmeyi kapattı; koşum arka planda tamamlanır.
    }
});

// Dağıtım script'i. scope: "modules" | "tables" | "all" (varsayılan all).
// Selection verilirse YALNIZCA işaretlenen objeler yazılır; boşsa tümü.
// dataLoss=true verilmedikçe kolon/tablo silme ve tip daraltma script'e GİRMEZ,
// ayrı listede raporlanır. Kapsam dışı kalan her şey ismen görünür, sessizce düşmez.
app.MapPost("/api/runs/{id}/script", (string id, ScriptRequest request, CompareService compare) =>
{
    var session = compare.Get(id);
    if (session?.Comparison is null) return Results.NotFound();

    var forwardCmp = session.Comparison;
    var reverseCmp = SchemaComparer.Compare(session.Comparison.Target, session.Comparison.Source);
    var generatedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
    var scope = request.Scope;
    var wantModules = scope is null or "all" or "modules";
    var wantTables = scope is null or "all" or "tables";
    var allowDataLoss = request.DataLoss;

    // İşaretlenen objeler → ObjectKey kümesi. Boş/yoksa null.
    ISet<ObjectKey>? Parse(SelectionItemDto[]? items)
    {
        if (items is not { Length: > 0 }) return null;
        var set = new HashSet<ObjectKey>(ObjectKeyComparer.CaseInsensitive);
        foreach (var pick in items)
            set.Add(new ObjectKey(pick.Schema, pick.Name, CompareService.ResolveKind(pick.ObjectType)));
        return set;
    }

    var sb = new System.Text.StringBuilder(16384);
    var included = 0;
    var outOfScope = 0;
    var skipped = new List<object>();
    var dataLossActions = new List<object>();
    var hadCycle = false;

    // Bir yön için tüm dilimleri (tip → tablo → modül → rol → EP/izin) doğru sırada yazar.
    void BuildBody(CompareResult comparison, ISet<ObjectKey>? selection)
    {
        // Kullanıcı tanımlı tipler EN BAŞTA: tablo ve modüller onlara bağlı olabilir.
        if (wantTables || wantModules)
        {
            var types = TypeScriptGenerator.Generate(comparison, selection,
                new TypeScriptOptions
                {
                    GeneratedAt = generatedAt,
                    IncludeDrops = request.DropNotInSource,
                    RecreateChangedTableTypes = request.RecreateChangedTableTypes,
                });
            if (!types.IsEmpty) { sb.AppendLine(types.Sql); sb.AppendLine(); included += types.Included.Count; }
            skipped.AddRange(types.Skipped.Select(s => (object)new { key = s.Key.ToString(), reason = s.Reason }));
        }

        if (wantTables)
        {
            var table = TableScriptGenerator.Generate(comparison, selection, new TableScriptOptions
            {
                GeneratedAt = generatedAt,
                AllowDataLoss = allowDataLoss,
                IncludeDrops = request.DropNotInSource,
                ValidateNewConstraints = request.ScriptValidateNewConstraints,
            });
            if (!table.IsEmpty) { sb.AppendLine(table.Sql); sb.AppendLine(); }
            included += table.Included.Count;
            hadCycle |= table.HadDependencyCycle;
            skipped.AddRange(table.Skipped.Select(s => (object)new { key = s.Key.ToString(), reason = s.Reason }));
            dataLossActions.AddRange(table.DataLossActions.Select(a =>
                (object)new { table = a.Table.ToString(), a.Column, a.Description, a.Sql }));
        }

        if (wantModules)
        {
            var module = ModuleScriptGenerator.Generate(comparison, selection, new ScriptOptions
            {
                GeneratedAt = generatedAt,
                TablesHandledElsewhere = wantTables,
                IncludeDrops = request.DropNotInSource,
                // Tip üretecinin listesinden türetilir; elle kopyalanınca sürekli ayrışıyordu.
                HandledElsewhere = new HashSet<ObjectKind>(TypeScriptGenerator.HandledKinds)
                {
                    ObjectKind.Role, ObjectKind.User, ObjectKind.PlanGuide,
                },
            });
            if (!module.IsEmpty) { sb.AppendLine(module.Sql); }
            included += module.Included.Count;
            outOfScope += module.OutOfScope.Count;
            hadCycle |= module.HadDependencyCycle;
            skipped.AddRange(module.Skipped.Select(s => (object)new { key = s.Key.ToString(), reason = s.Reason }));
        }

        if (wantModules)
        {
            var roles = RoleScriptGenerator.Generate(comparison, selection, new RoleScriptOptions { GeneratedAt = generatedAt, IncludeDrops = request.DropNotInSource });
            if (!roles.IsEmpty) { sb.AppendLine(); sb.AppendLine(roles.Sql); included += roles.Included.Count; }
            skipped.AddRange(roles.Skipped.Select(s => (object)new { key = s.Key.ToString(), reason = s.Reason }));
        }

        if (wantModules && wantTables)
        {
            var ep = ExtendedPropertyScriptGenerator.Generate(comparison, selection, generatedAt);
            if (!ep.IsEmpty) { sb.AppendLine(); sb.AppendLine(ep.Sql); included += ep.Included.Count; }
            skipped.AddRange(ep.Skipped.Select(s => (object)new { key = s.Key.ToString(), reason = s.Reason }));

            var perms = PermissionScriptGenerator.Generate(comparison, selection, generatedAt);
            if (!perms.IsEmpty) { sb.AppendLine(); sb.AppendLine(perms.Sql); included += perms.Included.Count; }
            skipped.AddRange(perms.Skipped.Select(s => (object)new { key = s.Key.ToString(), reason = s.Reason }));

            // Ayarlar ve plan guide'lar EN SONDA: OBJECT kapsamlı guide modüle bağlıdır.
            var settings = SettingsScriptGenerator.Generate(comparison, selection, generatedAt);
            if (!settings.IsEmpty) { sb.AppendLine(); sb.AppendLine(settings.Sql); included += settings.Included.Count; }
            skipped.AddRange(settings.Skipped.Select(s => (object)new { key = s.Key.ToString(), reason = s.Reason }));
        }
    }

    var fwd = Parse(request.Selection);
    var rev = Parse(request.ReverseSelection);

    // Kullanıcı ileri seçimin HEPSİNİN tikini kaldırdıysa (boş liste GÖNDERİLDİ): "hiçbiri".
    // Bu, "seçim yok (null) → tümü" varsayılanından farklıdır — boş liste açıkça "hiçbiri" demektir.
    var forwardExplicitEmpty = request.Selection is { Length: 0 };
    // İleri bölüm HEPSİNİ yazar: yalnız hiç seçim yoksa (null) ve ters seçim de yoksa ve
    // kullanıcı açıkça hepsini kaldırmadıysa.
    var forwardAll = fwd is null && rev is null && !forwardExplicitEmpty;

    // Script'e giren her gövdenin (ileri/ters) hedefinde DOLU olup bloklanacak tabloları
    // en başa uyarı olarak yazar. Deploy'dan önce görülmesi gereken tek şey bu.
    var warnSegments = new List<(CompareResult, ISet<ObjectKey>?)>();
    if (request.Reverse)
        warnSegments.Add((reverseCmp, fwd));
    else
    {
        if (forwardAll || fwd is not null) warnSegments.Add((forwardCmp, fwd));
        if (rev is not null) warnSegments.Add((reverseCmp, rev));
    }
    var (blockingCount, _) = DeploymentWarning.Build(warnSegments);

    // Bu script HANGİ DB üzerinde çalışır? Her BuildBody yönü cmp.Target'ı mutasyona uğratır:
    //   ileri → hedef DB ; tam ters (rollback) → kaynak DB.
    // Karışık (ileri + ⇄ ters bölüm) iki farklı DB'ye dokunur → tek USE olmaz, bölüm başına USE.
    var mixed = !request.Reverse && fwd is not null && rev is not null;
    string? headerDb =
        request.Reverse ? reverseCmp.Target.Database                       // tam ters → kaynak DB
        : mixed ? null                                                     // karışık → bölüm başına USE
        : rev is not null ? reverseCmp.Target.Database                     // yalnız ⇄ ters bölüm → kaynak DB
        : forwardCmp.Target.Database;                                      // ileri → hedef DB

    // Tam-ters (Reverse=true): seçili objeleri ters karşılaştırmadan üret.
    // Seçim BOŞ liste (forwardExplicitEmpty) ise "hiçbiri" → hiç yazma (reverse sekmesi boş kalır).
    // Seçim hiç yoksa (null) eski davranış: tümü.
    if (request.Reverse)
    {
        if (fwd is not null || !forwardExplicitEmpty) BuildBody(reverseCmp, fwd);
    }
    else
    {
        // İLERİ bölüm: seçili objeler (⇄ ile işaretlenenler UI'da zaten forward'dan çıkarılır).
        // forwardAll yukarıda hesaplandı: yalnız hiç seçim yoksa tümü; hepsi kaldırıldıysa hiçbiri.
        if (forwardAll || fwd is not null)
        {
            if (mixed) sb.Append(DeploymentHeader.UseDatabase(forwardCmp.Target.Database)).AppendLine();
            BuildBody(forwardCmp, fwd);
        }

        // GERİ ALMA bölümü: ⇄ ile işaretlenen objeler, ters yönde, aynı dosyaya eklenir.
        if (rev is not null)
        {
            sb.AppendLine();
            sb.AppendLine("/* ====================================================================");
            sb.AppendLine("   GERİ ALMA (REVERSE) BÖLÜMÜ — aşağıdaki objeler TERS yönde uygulanır");
            sb.AppendLine($"   (hedef → kaynak). Kaynak: {reverseCmp.Source.Database}  Hedef: {reverseCmp.Target.Database}");
            sb.AppendLine("   ==================================================================== */");
            sb.AppendLine();
            // Karışık script'te ileri bölüm hedef DB'de kaldı; ters bölüm kaynak DB'ye geçer.
            if (mixed) sb.Append(DeploymentHeader.UseDatabase(reverseCmp.Target.Database)).AppendLine();
            BuildBody(reverseCmp, rev);
        }
    }

    // En üste yalnızca USE [hedef DB] yazılır (yorum/uyarı bloğu yok — production için sade).
    // Bloklama uyarısı yalnızca arayüzde gösterilir (blockingCount), script'e girmez.
    if (sb.Length > 0) sb.Insert(0, DeploymentHeader.Master(forwardCmp, generatedAt, allowDataLoss, headerDb));

    var tag = request.Reverse ? "reverse" : (rev is not null ? "ileri+reverse" : (fwd is null ? scope ?? "all" : "secili"));
    var fileName = $"{forwardCmp.Target.Database}_{tag}_{DateTime.Now:yyyyMMdd-HHmm}.sql";

    return Results.Ok(new
    {
        fileName,
        sql = sb.ToString().TrimEnd() + Environment.NewLine,
        included,
        outOfScope,
        skipped,
        dataLossActions,
        hadDependencyCycle = hadCycle,
        selectedCount = (fwd?.Count ?? 0) + (rev?.Count ?? 0),
        reverseCount = rev?.Count ?? 0,
        blockingCount,
    });
});

app.MapGet("/api/runs/{id}/detail", (
    string id, string schema, string name, string kind, CompareService compare) =>
{
    var session = compare.Get(id);
    if (session is null) return Results.NotFound();

    var detail = compare.Detail(session, schema, name, kind);
    return detail is null ? Results.NotFound() : Results.Ok(detail);
});

try
{
    app.Run();
}
catch (IOException ex) when (ex.InnerException is AddressInUseException)
{
    // Çıplak yığın izi yerine ne yapılacağını söyle.
    Console.Error.WriteLine($"""

        HATA: {port} portu zaten kullanımda.

        Muhtemelen SchemaDiff.Web zaten çalışıyor — önce http://127.0.0.1:{port} adresini deneyin.

        Portu kimin tuttuğunu görmek için:
            Get-NetTCPConnection -LocalPort {port} | Select-Object OwningProcess
        Başka bir port kullanmak için:
            dotnet run -c Release -- --port 5300
        """);
    return 2;
}

return 0;

/// <summary>
/// İstemciden gelen alanları bağlantı bilgisine çevirir. Parola boş bırakılmış ve
/// kayıtlı bir bağlantı seçilmişse, parola sunucu tarafında çözülür — tarayıcıya
/// hiçbir zaman gönderilmez.
/// </summary>
static SqlConnectionInfo ToConnectionInfo(ConnectionDto dto, RecentConnections recent)
{
    var authentication = string.Equals(dto.Authentication, "SqlLogin", StringComparison.OrdinalIgnoreCase)
        ? SqlAuthentication.SqlLogin
        : SqlAuthentication.Windows;

    var password = dto.Password;
    if (string.IsNullOrEmpty(password) && authentication == SqlAuthentication.SqlLogin)
        password = recent.ResolvePassword(dto.Id);

    return new SqlConnectionInfo
    {
        Server = dto.Server.Trim(),
        Database = string.IsNullOrWhiteSpace(dto.Database) ? null : dto.Database.Trim(),
        Authentication = authentication,
        UserName = dto.UserName,
        Password = password,
        Encrypt = dto.Encrypt,
        TrustServerCertificate = dto.TrustServerCertificate,
    };
}

static async Task WriteEventAsync(
    HttpResponse response, string name, object payload, JsonSerializerOptions options, CancellationToken ct)
{
    var json = JsonSerializer.Serialize(payload, options);
    await response.WriteAsync($"event: {name}\ndata: {json}\n\n", ct);
    await response.Body.FlushAsync(ct);
}

static int ResolvePort(string[] args)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i] is "--port" or "-p" && int.TryParse(args[i + 1], out var parsed))
            return parsed;

    return 5290;
}
