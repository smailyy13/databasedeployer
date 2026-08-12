using System.Globalization;
using Microsoft.Data.SqlClient;

namespace SchemaDiff.Core.Extraction;

// Katalog view'larından okunan ham satırlar. Bilinçli olarak "aptal" tutuluyor:
// yorumlama ve kanonikleştirme SnapshotBuilder'ın işi.

internal sealed record SchemaRow(int SchemaId, string Name);

/// <summary>class: 1 = obje/kolon (major=object_id, minor=column_id ya da 0), 3 = şema (major=schema_id).</summary>
internal sealed record ExtendedPropertyRow(
    byte Class, int MajorId, int MinorId, string Name, string? Value);

internal sealed record UserDefinedTypeRow(
    int UserTypeId, string SchemaName, string Name, string BaseType,
    short MaxLength, byte Precision, byte Scale, bool IsNullable, string? Collation);

internal sealed record TableTypeRow(int UserTypeId, string SchemaName, string Name, int TypeTableObjectId);

internal sealed record TableTypeColumnRow(
    int ObjectId, int ColumnId, string Name, string TypeSchema, string TypeName,
    short MaxLength, byte Precision, byte Scale, bool IsNullable, string? Collation,
    bool IsIdentity, bool IsComputed);

internal sealed record PartitionFunctionRow(
    int FunctionId, string Name, bool BoundaryOnRight, string? InputType);

internal sealed record PartitionRangeValueRow(int FunctionId, int BoundaryId, string? Value);

internal sealed record PartitionSchemeRow(int SchemeId, string Name, string FunctionName);

internal sealed record PartitionSchemeFileRow(int SchemeId, int DestinationId, string Filegroup);

internal sealed record RoleRow(int PrincipalId, string Name, string? Owner, bool IsFixed = false);

/// <summary>Veritabanı kullanıcısı. Tip: S=SQL, U=Windows kullanıcı, G=Windows grup, E/X=external.</summary>
internal sealed record UserRow(string Name, string Type, string? DefaultSchema);

internal sealed record RoleMemberRow(int RolePrincipalId, string? MemberName);

/// <summary>class 1 = obje/kolon, 3 = şema. State: GRANT / GRANT_WITH_GRANT_OPTION / DENY.</summary>
internal sealed record PermissionRow(
    byte Class, int MajorId, int MinorId, string PermissionName, string State, string? Grantee);

internal sealed record ObjectRow(
    int ObjectId, string SchemaName, string Name, string Type,
    DateTime ModifyDate, int ParentObjectId);

internal sealed record ModuleRow(
    int ObjectId, string? Definition, bool UsesAnsiNulls, bool UsesQuotedIdentifier);

internal sealed record ColumnRow(
    int ObjectId, int ColumnId, string Name,
    string TypeSchema, string TypeName,
    short MaxLength, byte Precision, byte Scale,
    bool IsNullable, string? Collation, bool IsIdentity, bool IsComputed,
    string? DefaultName, string? DefaultDefinition, bool? DefaultIsSystemNamed,
    string? ComputedDefinition, bool? ComputedIsPersisted,
    string? IdentitySeed, string? IdentityIncrement,
    byte GeneratedAlwaysType = 0, bool IsHidden = false,
    bool IsSparse = false, bool IsFileStream = false, bool IsRowGuidCol = false, bool IsColumnSet = false,
    int XmlCollectionId = 0, bool IsXmlDocument = false, bool IdentityNotForReplication = false);

/// <summary>Tipli XML kolonlarının başvurduğu şema koleksiyonu — id'den ada çözüm için.</summary>
internal sealed record XmlSchemaCollectionRow(int CollectionId, string SchemaName, string Name);

internal sealed record IndexRow(
    int ObjectId, int IndexId, string? Name, string TypeDesc,
    bool IsUnique, bool IsPrimaryKey, bool IsUniqueConstraint,
    byte FillFactor, bool IsPadded, bool IgnoreDupKey, string? FilterDefinition,
    string? DataCompression = null,
    bool AllowRowLocks = true, bool AllowPageLocks = true);

internal sealed record IndexColumnRow(
    int ObjectId, int IndexId, int IndexColumnId, int ColumnId,
    byte KeyOrdinal, bool IsDescending, bool IsIncluded);

/// <summary>
/// Kullanıcının CREATE STATISTICS ile oluşturduğu istatistik. Otomatik üretilenler
/// (auto_created) ve index'in taşıdıkları şema farkı değildir — sorgu onları getirmez.
/// </summary>
internal sealed record StatisticRow(
    int ObjectId, int StatsId, string Name, bool NoRecompute, string? FilterDefinition, bool IsIncremental);

internal sealed record StatisticColumnRow(int ObjectId, int StatsId, int StatsColumnId, int ColumnId);

internal sealed record KeyConstraintRow(
    int ParentObjectId, int? UniqueIndexId, string Name, string Type, bool IsSystemNamed);

internal sealed record ForeignKeyRow(
    int ObjectId, int ParentObjectId, string Name, bool IsSystemNamed, int ReferencedObjectId,
    byte DeleteAction, byte UpdateAction, bool IsDisabled, bool IsNotTrusted,
    bool IsNotForReplication = false);

internal sealed record ForeignKeyColumnRow(
    int ConstraintObjectId, int ConstraintColumnId,
    int ParentObjectId, int ParentColumnId,
    int ReferencedObjectId, int ReferencedColumnId);

internal sealed record CheckConstraintRow(
    int ParentObjectId, string Name, bool IsSystemNamed,
    string? Definition, bool IsDisabled, bool IsNotTrusted, bool IsNotForReplication = false);

internal sealed record SynonymRow(int ObjectId, string BaseObjectName);

internal sealed record SequenceRow(
    int ObjectId, string TypeName, byte Precision, byte Scale,
    string? StartValue, string? Increment, string? MinValue, string? MaxValue,
    bool IsCycling, bool IsCached, int? CacheSize);

internal sealed record TriggerRow(int ObjectId, bool IsDisabled, bool IsInsteadOf);

/// <summary>Veritabanı seviyesi DDL trigger (parent_class=0). Şema yok, ad benzersiz.</summary>
internal sealed record DdlTriggerRow(
    string Name, bool IsDisabled, string? Definition, bool UsesAnsiNulls, bool UsesQuotedIdentifier);

internal sealed record RowCountRow(int ObjectId, long Rows);

/// <summary>System-versioned temporal tablo: history tablosu ve PERIOD kolonları.</summary>
internal sealed record TemporalRow(
    int ObjectId, string? HistorySchema, string? HistoryName, string? StartColumn, string? EndColumn);

internal sealed record DependencyRow(int ReferencingId, int ReferencedId);

/// <summary>Dış (cross-db/linked server) referans: kaynak obje → hedef sunucu/db/şema/obje.</summary>
internal sealed record ExternalReferenceRow(
    string SourceSchema, string SourceName, string Server, string Database, string RefSchema, string RefEntity);

/// <summary>SqlDataReader okuma yardımcıları. SequentialAccess kullandığımız için
/// tüm mapper'lar ordinalleri artan sırada okumak zorunda.</summary>
internal static class Rdr
{
    public static string Str(SqlDataReader r, int i) => r.GetString(i);
    public static string? NStr(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    public static bool Bool(SqlDataReader r, int i) => r.GetBoolean(i);
    public static bool? NBool(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetBoolean(i);
    public static int Int(SqlDataReader r, int i) => r.GetInt32(i);
    public static int? NInt(SqlDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt32(i);
    public static short Short(SqlDataReader r, int i) => r.GetInt16(i);
    public static long Long(SqlDataReader r, int i) => r.GetInt64(i);
    public static byte Byte(SqlDataReader r, int i) => r.IsDBNull(i) ? (byte)0 : r.GetByte(i);
    public static DateTime Date(SqlDataReader r, int i) => r.GetDateTime(i);

    /// <summary>sql_variant kolonları (identity seed/increment) için.</summary>
    public static string? Variant(SqlDataReader r, int i) =>
        r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture);
}
