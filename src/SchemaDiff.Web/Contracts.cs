namespace SchemaDiff.Web;

// Tarayıcıya giden tipler. Parola YALNIZCA istemciden sunucuya gider; hiçbir
// yanıtta geri dönmez.

public sealed record ConnectionDto(
    string Server,
    string? Database = null,
    string Authentication = "Windows",
    string? UserName = null,
    string? Password = null,
    bool RememberPassword = false,
    bool Encrypt = false,
    bool TrustServerCertificate = true,
    string? Id = null);

public sealed record RecentConnectionDto(
    string Id,
    string Server,
    string? Database,
    string Authentication,
    string? UserName,
    bool Encrypt,
    bool TrustServerCertificate,
    bool HasStoredPassword,
    string Label,
    DateTimeOffset LastUsed);

/// <summary>SSDT'nin Schema Compare seçenekleriyle aynı hizada. Yalnızca gerçekten
/// uygulanan seçenekler yer alır — çalışmayan bir kutu göstermek yanıltıcı olur.</summary>
public sealed record CompareOptionsDto(
    bool IgnoreWhitespace = true,
    bool IgnoreComments = true,
    bool IgnoreKeywordCasing = false,
    bool IgnoreSemicolons = false,
    bool IgnoreColumnOrder = false,
    bool IgnoreCollation = false,
    bool IgnoreIdentitySeed = false,
    bool IgnoreIdentityIncrement = false,
    bool IgnoreIndexPhysical = false,
    bool IgnoreFillFactor = true,
    bool IgnoreIndexPadding = false,
    bool IgnoreDataCompression = false,
    bool IgnoreStatistics = false,
    bool IgnoreAnsiNulls = true,
    bool IgnoreQuotedIdentifiers = true,
    bool IgnoreDmlTriggerState = false,
    bool IgnoreSystemNamedConstraints = true,
    bool IgnoreExtendedProperties = false,
    bool IgnorePermissions = false,
    bool CaseSensitiveNames = false,
    bool CaseSensitiveColumnNames = false,
    int MaxQueries = 16);

public sealed record CompareRequest(
    ConnectionDto Source,
    ConnectionDto Target,
    CompareOptionsDto? Options);

/// <summary>Script üretimi isteği. Selection null ise (alan hiç gönderilmedi) TÜM değişiklikler
/// yazılır; BOŞ liste gönderilirse "hiçbiri" demektir → ileri bölüm boş kalır.
/// ReverseSelection'daki objeler için ise GERİ ALMA (target→source) kodu üretilir ve
/// aynı dosyaya, ileri bölümün ardından eklenir. Reverse=true tüm isteği tersine çevirir
/// (eski davranış; artık UI ReverseSelection kullanıyor).</summary>
public sealed record ScriptRequest(
    string? Scope = null,
    bool DataLoss = false,
    SelectionItemDto[]? Selection = null,
    bool Reverse = false,
    SelectionItemDto[]? ReverseSelection = null,
    bool DropNotInSource = true,
    bool ScriptValidateNewConstraints = true,
    bool RecreateChangedTableTypes = false);

/// <summary>Kullanıcının işaretlediği bir obje. ObjectType arayüzdeki görünen türdür
/// ("Table", "Scalar Function", "Role", "Schema" …), sunucuda ObjectKind'e çevrilir.</summary>
public sealed record SelectionItemDto(string ObjectType, string Schema, string Name);

public sealed record ChildChangeDto(
    string Action, string Category, string ItemType, string Name, string QualifiedName, string? Detail);

public sealed record ObjectChangeDto(
    string Action,
    string ObjectType,
    string Schema,
    string Name,
    ChildChangeDto[] Children,
    string Risk,
    long? Rows,
    bool WillBlock,
    bool ConditionalOnly,
    bool Indeterminate,
    string? Note);

public sealed record RiskFindingDto(string Column, string Risk, string Description, bool Conditional);

public sealed record TableRiskDto(
    string Schema, string Name, string Risk, bool WillBlock, bool ConditionalOnly, long? Rows, RiskFindingDto[] Findings);

public sealed record TriggerDto(string Schema, string Name, bool Disabled, bool? SourceDisabled);

public sealed record RenameDto(string Removed, string Added, string Basis);

public sealed record CompareResultDto(
    string RunId,
    string SourceLabel,
    string TargetLabel,
    int SourceObjects,
    int TargetObjects,
    int Equal,
    int AddCount,
    int ChangeCount,
    int DeleteCount,
    int IndeterminateCount,
    double DurationMs,
    string[] Warnings,
    ObjectChangeDto[] Changes,
    bool Truncated,
    TableRiskDto[] Risks,
    TriggerDto[] Triggers,
    RenameDto[] Renames);

/// <summary>
/// Bir objenin iki taraftaki metni — alt paneldeki "Object Definitions" için.
/// Script alanları tam metindir (modülde özgün gövde, tabloda üretilmiş CREATE);
/// Text alanları karşılaştırmanın kullandığı kanonik biçimdir.
/// </summary>
public sealed record ObjectDetailDto(
    string Schema, string Name, string Kind,
    string? SourceText, string? TargetText,
    string? SourceScript, string? TargetScript);
