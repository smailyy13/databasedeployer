using System.Text.Json;
using System.Text.Json.Serialization;

namespace SchemaDiff.Core.Jobs;

/// <summary>
/// Karşılaştırılacak çiftlerin tanımlandığı dosya. Hangi veritabanlarının
/// karşılaştırılacağı bir yapılandırma sorusudur, kod sorusu değil.
/// </summary>
public sealed class JobConfiguration
{
    /// <summary>Her işte tekrar yazmamak için ortak bağlantı dizeleri.</summary>
    public ConnectionDefaults? Defaults { get; set; }

    public List<JobDefinition> Jobs { get; set; } = [];

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static async Task<IReadOnlyList<ComparisonJob>> LoadAsync(string path, CancellationToken ct = default)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Yapılandırma dosyası bulunamadı: {path}", path);

        await using var stream = File.OpenRead(path);
        var configuration = await JsonSerializer.DeserializeAsync<JobConfiguration>(stream, ReadOptions, ct)
            ?? throw new InvalidDataException($"Yapılandırma dosyası boş ya da okunamadı: {path}");

        return configuration.Resolve();
    }

    public IReadOnlyList<ComparisonJob> Resolve()
    {
        if (Jobs.Count == 0)
            throw new InvalidDataException("Yapılandırmada hiç iş tanımlı değil ('jobs' listesi boş).");

        var resolved = new List<ComparisonJob>(Jobs.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < Jobs.Count; i++)
        {
            var job = Jobs[i];
            var position = $"jobs[{i}]";

            var source = job.Source ?? Defaults?.Source
                ?? throw new InvalidDataException(
                    $"{position}: 'source' verilmemiş ve 'defaults.source' da tanımlı değil.");

            var target = job.Target ?? Defaults?.Target
                ?? throw new InvalidDataException(
                    $"{position}: 'target' verilmemiş ve 'defaults.target' da tanımlı değil.");

            var sourceDatabase = job.SourceDatabase ?? job.Database;
            var targetDatabase = job.TargetDatabase ?? job.Database;

            if (sourceDatabase is not null) source = ComparisonJob.WithDatabase(source, sourceDatabase);
            if (targetDatabase is not null) target = ComparisonJob.WithDatabase(target, targetDatabase);

            var name = job.Name ?? job.Database ?? sourceDatabase ?? $"job{i + 1}";

            if (!seen.Add(name))
                throw new InvalidDataException(
                    $"{position}: '{name}' adı birden fazla işte kullanılmış. " +
                    "İş adları seçim ve raporlamada anahtar olarak kullanıldığı için benzersiz olmalı.");

            resolved.Add(new ComparisonJob(name, source, target));
        }

        return resolved;
    }

    /// <summary>Kullanıcının düzenleyebileceği bir başlangıç dosyası üretir.</summary>
    public static async Task WriteTemplateAsync(string path, CancellationToken ct = default)
    {
        var template = new JobConfiguration
        {
            Defaults = new ConnectionDefaults
            {
                Source = @"Server=KAYNAK_SUNUCU;Integrated Security=true;TrustServerCertificate=true",
                Target = @"Server=HEDEF_SUNUCU;Integrated Security=true;TrustServerCertificate=true",
            },
            Jobs =
            [
                new JobDefinition { Database = "VeritabaniAdi1" },
                new JobDefinition { Database = "VeritabaniAdi2" },
                new JobDefinition
                {
                    Name = "farkli-adlar-ornegi",
                    SourceDatabase = "KaynaktakiAd",
                    TargetDatabase = "HedeftekiAd",
                },
                new JobDefinition
                {
                    Name = "farkli-sunucu-ornegi",
                    Source = "Server=BASKA_SUNUCU;Database=X;Integrated Security=true;TrustServerCertificate=true",
                    Target = "Server=YINE_BASKA;Database=Y;Integrated Security=true;TrustServerCertificate=true",
                },
            ],
        };

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, template, WriteOptions, ct);
    }
}

public sealed class ConnectionDefaults
{
    public string? Source { get; set; }
    public string? Target { get; set; }
}

public sealed class JobDefinition
{
    /// <summary>Seçimde ve raporda kullanılan ad. Verilmezse veritabanı adından türetilir.</summary>
    public string? Name { get; set; }

    /// <summary>Tam bağlantı dizesi. Verilirse defaults yerine geçer.</summary>
    public string? Source { get; set; }

    public string? Target { get; set; }

    /// <summary>İki tarafta da aynı olan veritabanı adı — en yaygın durum için kısayol.</summary>
    public string? Database { get; set; }

    /// <summary>İki taraf farklı adlandırılmışsa.</summary>
    public string? SourceDatabase { get; set; }

    public string? TargetDatabase { get; set; }
}
