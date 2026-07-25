using System.Diagnostics.CodeAnalysis;

namespace SchemaDiff.Core.Model;

/// <summary>
/// Tek bir şema objesinin karşılaştırmaya hazır hâli. Hash, "değişti mi?" sorusunu
/// metne hiç bakmadan cevaplar; Canonical yalnızca hash tutmayan objelerin detay
/// diff'i için kullanılır.
/// </summary>
public sealed class ObjectSnapshot
{
    public required ObjectKey Key { get; init; }
    public required UInt128 Hash { get; set; }
    public DateTime ModifyDate { get; init; }

    /// <summary>Objenin kanonik metni (normalize edilmiş). Detay diff burada üretilir.</summary>
    public string Canonical { get; set; } = string.Empty;

    /// <summary>
    /// Alt-parça hash'leri ("columns", "indexes", "constraints", "foreignKeys", "body").
    /// Bir tablonun neresinin değiştiğini diff hesaplamadan söyleyebilmek için.
    /// </summary>
    public Dictionary<string, UInt128> Parts { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, string> PartCanonical { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Tanımı okunamayan obje (şifrelenmiş ya da VIEW DEFINITION yetkisi yok).
    /// Böyle objeler "aynı" sayılmaz — ayrı raporlanır, çünkü sessiz eşitlik en tehlikeli sonuçtur.
    /// </summary>
    public bool Incomparable { get; set; }

    public string? IncomparableReason { get; set; }

    /// <summary>Tablolar için yapısal kolon bilgisi. Veri kaybı riski analizi buna dayanır.</summary>
    public IReadOnlyList<ColumnInfo>? Columns { get; set; }

    /// <summary>
    /// Tablonun yaklaşık satır sayısı (sys.dm_db_partition_stats — tarama yapmaz).
    /// "Bu değişiklik deploy'da bloklanır mı?" sorusunu deploy'dan ÖNCE cevaplar.
    /// </summary>
    public long? RowCount { get; set; }

    /// <summary>Trigger'lar için etkin/pasif durumu.</summary>
    public bool? IsDisabled { get; set; }

    /// <summary>
    /// Objenin arayüzde gösterilecek tam metni. Modüllerde normalize edilmemiş özgün
    /// gövde, tablolarda katalogdan üretilmiş CREATE script'i. Yalnızca
    /// <see cref="Extraction.SnapshotOptions.KeepDisplayScripts"/> açıkken doldurulur.
    ///
    /// Kanonik metin karşılaştırma için yeterli ama okunmak için değil: modüllerde tek
    /// satıra iner, tablolarda ise yalnızca alan listesidir. Değişikliği öncesi ve
    /// sonrasıyla görebilmek için tam metin gerekir.
    /// </summary>
    public string? DisplayScript { get; set; }

    /// <summary>Modülün oluşturulduğu SET seçenekleri — dağıtım script'inde aynen kurulmalı.</summary>
    public bool? UsesAnsiNulls { get; set; }

    public bool? UsesQuotedIdentifier { get; set; }

    /// <summary>Trigger'ın bağlı olduğu tablo/view. Script üretiminde ön koşul kontrolü için.</summary>
    public ObjectKey? Parent { get; set; }

    /// <summary>
    /// Tablonun index'leri (PK/UQ dahil) — yapısal, script üretimi için. Yalnızca
    /// <see cref="Extraction.SnapshotOptions.KeepDisplayScripts"/> açıkken doldurulur.
    /// </summary>
    public IReadOnlyList<IndexDefinition>? IndexDefinitions { get; set; }

    public IReadOnlyList<CheckDefinition>? CheckDefinitions { get; set; }

    public IReadOnlyList<ForeignKeyDefinition>? ForeignKeyDefinitions { get; set; }
}

/// <summary>Script üretimi için index'in yapısal tanımı.</summary>
public sealed record IndexDefinition(
    string Name,
    bool IsPrimaryKey,
    bool IsUniqueConstraint,
    bool IsUnique,
    string TypeDesc,
    bool IsSystemNamed,
    IReadOnlyList<IndexKeyColumn> KeyColumns,
    IReadOnlyList<string> IncludedColumns,
    string? FilterDefinition)
{
    /// <summary>PK ve UNIQUE constraint'ler ALTER TABLE ADD CONSTRAINT ile yazılır; ötekiler CREATE INDEX.</summary>
    public bool IsConstraint => IsPrimaryKey || IsUniqueConstraint;
}

public sealed record IndexKeyColumn(string Column, bool Descending);

public sealed record CheckDefinition(string Name, string Definition, bool IsSystemNamed);

public sealed record ForeignKeyDefinition(
    string Name,
    bool IsSystemNamed,
    string ReferencedSchema,
    string ReferencedName,
    IReadOnlyList<ForeignKeyColumnPair> Columns,
    byte DeleteAction,
    byte UpdateAction);

public sealed record ForeignKeyColumnPair(string Parent, string Referenced);

/// <summary>Risk analizi için gereken kolon özellikleri.</summary>
public sealed record ColumnInfo(
    string Name,
    string TypeSchema,
    string TypeName,
    short MaxLength,
    byte Precision,
    byte Scale,
    bool IsNullable,
    string? Collation,
    bool IsIdentity,
    bool IsComputed,
    string? DefaultDefinition,
    string? DefaultName = null,
    bool DefaultIsSystemNamed = false)
{
    public string TypeDisplay => MaxLength switch
    {
        -1 => $"{TypeName}(max)",
        _ when TypeName is "decimal" or "numeric" => $"{TypeName}({Precision},{Scale})",
        _ when TypeName is "nvarchar" or "nchar" => $"{TypeName}({MaxLength / 2})",
        _ when TypeName is "varchar" or "char" or "varbinary" or "binary" => $"{TypeName}({MaxLength})",
        _ => TypeName,
    };
}

public sealed class DatabaseSnapshot
{
    public required string Server { get; init; }
    public required string Database { get; init; }
    public required Dictionary<ObjectKey, ObjectSnapshot> Objects { get; init; }
    public ExtractionReport Report { get; set; } = new();

    /// <summary>
    /// Obje → referans verdiği objeler. Script üretiminde yeni objeleri doğru sırada
    /// oluşturmak için kullanılır; karşılaştırmaya girmez.
    /// </summary>
    public Dictionary<ObjectKey, List<ObjectKey>> References { get; init; } = [];

    public int Count => Objects.Count;
}

/// <summary>Çekim aşamasının ölçümleri ve uyarıları. PoC'nin asıl çıktısı bu.</summary>
public sealed class ExtractionReport
{
    public Dictionary<string, TimeSpan> QueryTimings { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> RowCounts { get; } = new(StringComparer.Ordinal);
    public TimeSpan TotalExtraction { get; set; }
    public TimeSpan Normalization { get; set; }
    public List<string> Warnings { get; } = [];
}

public enum DiffKind { Equal, Added, Removed, Changed, Indeterminate }

public sealed record ObjectDiff(
    ObjectKey Key,
    DiffKind Kind,
    IReadOnlyList<string> ChangedParts);

public sealed class CompareResult
{
    public required DatabaseSnapshot Source { get; init; }
    public required DatabaseSnapshot Target { get; init; }
    public required List<ObjectDiff> Differences { get; init; }
    public int EqualCount { get; init; }
    public TimeSpan CompareTime { get; set; }

    public int AddedCount => Differences.Count(d => d.Kind == DiffKind.Added);
    public int RemovedCount => Differences.Count(d => d.Kind == DiffKind.Removed);
    public int ChangedCount => Differences.Count(d => d.Kind == DiffKind.Changed);
    public int IndeterminateCount => Differences.Count(d => d.Kind == DiffKind.Indeterminate);

    [SuppressMessage("Design", "CA1024", Justification = "Hesaplama içeriyor.")]
    public int TotalCompared() => EqualCount + Differences.Count;
}
