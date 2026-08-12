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

    /// <summary>Kullanıcı istatistikleri (CREATE STATISTICS) — script üretimi için.</summary>
    public IReadOnlyList<StatisticsDefinition>? StatisticsDefinitions { get; set; }

    /// <summary>Tablonun full-text index'i (en fazla bir tane) — script üretimi için.</summary>
    public FullTextIndexDefinition? FullTextIndex { get; set; }

    /// <summary>XML ve spatial index'ler — sözdizimleri genel index'ten tamamen farklı.</summary>
    public IReadOnlyList<XmlIndexDefinition>? XmlIndexes { get; set; }

    public IReadOnlyList<SpatialIndexDefinition>? SpatialIndexes { get; set; }
}

/// <summary>
/// Kullanıcı tanımlı istatistik. Örnekleme oranı (FULLSCAN / SAMPLE n PERCENT) katalogda
/// TUTULMAZ — yalnızca verinin o anki hâlini anlatan DMV'lerde bulunur ve şema farkı
/// değildir; bu yüzden script'te de yer almaz (SSDT de yazmaz).
/// </summary>
public sealed record StatisticsDefinition(
    string Name,
    IReadOnlyList<string> Columns,
    string? FilterDefinition,
    bool NoRecompute,
    bool IsIncremental);

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
    string? FilterDefinition,
    string? DataCompression = null,
    bool AllowRowLocks = true,
    bool AllowPageLocks = true)
{
    /// <summary>PK ve UNIQUE constraint'ler ALTER TABLE ADD CONSTRAINT ile yazılır; ötekiler CREATE INDEX.</summary>
    public bool IsConstraint => IsPrimaryKey || IsUniqueConstraint;

    /// <summary>Script'e açıkça yazılacak compression (ROW/PAGE/COLUMNSTORE_ARCHIVE); yoksa null.</summary>
    public string? ExplicitCompression =>
        DataCompression is "ROW" or "PAGE" or "COLUMNSTORE_ARCHIVE" ? DataCompression : null;
}

public sealed record IndexKeyColumn(string Column, bool Descending);

/// <summary>
/// XML index. Primary'de <c>CREATE PRIMARY XML INDEX</c>, secondary'de
/// <c>CREATE XML INDEX … USING XML INDEX [primary] FOR PATH|VALUE|PROPERTY</c> yazılır.
/// Kolonlarda ASC/DESC YOKTUR — genel index yazımı burada geçersiz SQL üretir.
/// </summary>
public sealed record XmlIndexDefinition(
    string Name, string Column, bool IsPrimary, string? SecondaryType, string? PrimaryIndexName);

/// <summary>
/// Spatial index. GEOMETRY_GRID <c>BOUNDING_BOX</c> ister, GEOGRAPHY_GRID istemez;
/// AUTO_GRID çeşitlerinde <c>GRIDS</c> yazılmaz (SQL Server kendisi seçer).
/// </summary>
public sealed record SpatialIndexDefinition(
    string Name, string Column, string Tessellation,
    double? BoundingXMin, double? BoundingYMin, double? BoundingXMax, double? BoundingYMax,
    string? Level1, string? Level2, string? Level3, string? Level4, int? CellsPerObject)
{
    public bool IsAutoGrid => Tessellation.Contains("AUTO_GRID", StringComparison.OrdinalIgnoreCase);

    public bool HasBoundingBox =>
        BoundingXMin is not null && BoundingYMin is not null &&
        BoundingXMax is not null && BoundingYMax is not null &&
        Tessellation.StartsWith("GEOMETRY", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Tablonun full-text index'i. Tablo başına en fazla bir tane olduğu için ayrı obje değil,
/// tablonun parçasıdır. KEY INDEX zorunludur: benzersiz, tek kolonlu, NOT NULL bir index.
/// </summary>
public sealed record FullTextIndexDefinition(
    string KeyIndexName,
    string CatalogName,
    IReadOnlyList<FullTextIndexColumn> Columns,
    string ChangeTracking,
    string Stoplist,
    bool IsEnabled);

/// <param name="TypeColumn">Binary kolonun uzantısını tutan kolon (TYPE COLUMN); yoksa null.</param>
/// <param name="LanguageId">Dil LCID'si; 0 ise sunucu varsayılanı kullanılır, yazılmaz.</param>
public sealed record FullTextIndexColumn(string Column, string? TypeColumn, int LanguageId);

public sealed record CheckDefinition(
    string Name, string Definition, bool IsSystemNamed, bool NotForReplication = false,
    bool IsDisabled = false, bool IsNotTrusted = false);

public sealed record ForeignKeyDefinition(
    string Name,
    bool IsSystemNamed,
    string ReferencedSchema,
    string ReferencedName,
    IReadOnlyList<ForeignKeyColumnPair> Columns,
    byte DeleteAction,
    byte UpdateAction,
    bool NotForReplication = false,
    bool IsDisabled = false,
    bool IsNotTrusted = false);

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
    bool DefaultIsSystemNamed = false,
    bool IsSparse = false,
    bool IsFileStream = false,
    bool IsRowGuidCol = false,
    bool IsColumnSet = false,
    string? XmlCollection = null,
    bool IsXmlDocument = false,
    bool IdentityNotForReplication = false,
    string? IdentitySeed = null,
    string? IdentityIncrement = null)
{
    /// <summary>Tipli XML kolonunun tip eki: <c>(CONTENT [şema].[koleksiyon])</c>; tipsizse null.</summary>
    public string? XmlTypeSuffix =>
        XmlCollection is null ? null : $"({(IsXmlDocument ? "DOCUMENT" : "CONTENT")} {XmlCollection})";

    public string TypeDisplay => XmlCollection is not null ? $"xml {XmlTypeSuffix}" : MaxLength switch
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
    /// Bu snapshot çıkarılırken kolon sırası hash'e girmedi mi (kullanıcı "kolon sırasını
    /// yok say" dedi mi). Risk analizinin, kullanıcının bilerek yok saydığı bir farkı geri
    /// gündeme getirmemesi için gerekir.
    /// </summary>
    public bool IgnoredColumnOrder { get; init; }

    /// <summary>Bu snapshot çıkarılırken collation hash'e girmedi mi ("collation'ı yok say").</summary>
    public bool IgnoredCollation { get; init; }

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

    /// <summary>
    /// Çalıştırılamayan (opsiyonel) sorguların adları. "Veri yok" ile "veriyi okuyamadık"
    /// ayrımı kritiktir: okunamayan bir sınıfı boş saymak, hedefte var olan objeler için
    /// DROP üretmeye kadar gider.
    /// </summary>
    public HashSet<string> FailedQueries { get; } = new(StringComparer.Ordinal);
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
