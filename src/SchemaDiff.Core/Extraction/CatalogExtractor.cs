using System.Data;
using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Extraction;

internal sealed class CatalogSet
{
    public List<SchemaRow> Schemas { get; set; } = [];
    public List<ObjectRow> Objects { get; set; } = [];
    public List<ModuleRow> Modules { get; set; } = [];
    public List<ColumnRow> Columns { get; set; } = [];
    public List<IndexRow> Indexes { get; set; } = [];
    public List<IndexColumnRow> IndexColumns { get; set; } = [];
    public List<XmlSchemaCollectionRow> XmlSchemaCollections { get; set; } = [];
    public List<XmlSchemaNamespaceRow> XmlSchemaNamespaces { get; set; } = [];
    public List<XmlSchemaContentRow> XmlSchemaContents { get; set; } = [];
    public List<IndexExtraRow> IndexExtras { get; set; } = [];
    public List<XmlIndexRow> XmlIndexes { get; set; } = [];
    public List<SpatialIndexRow> SpatialIndexes { get; set; } = [];
    public List<FullTextStoplistRow> FullTextStoplists { get; set; } = [];
    public List<FullTextStopwordRow> FullTextStopwords { get; set; } = [];
    public List<FullTextCatalogRow> FullTextCatalogs { get; set; } = [];
    public List<FullTextIndexRow> FullTextIndexes { get; set; } = [];
    public List<FullTextIndexColumnRow> FullTextIndexColumns { get; set; } = [];
    public List<StatisticRow> Statistics { get; set; } = [];
    public List<StatisticColumnRow> StatisticColumns { get; set; } = [];
    public List<KeyConstraintRow> KeyConstraints { get; set; } = [];
    public List<ForeignKeyRow> ForeignKeys { get; set; } = [];
    public List<ForeignKeyColumnRow> ForeignKeyColumns { get; set; } = [];
    public List<CheckConstraintRow> CheckConstraints { get; set; } = [];
    public List<SynonymRow> Synonyms { get; set; } = [];
    public List<SequenceRow> Sequences { get; set; } = [];
    public List<TriggerRow> Triggers { get; set; } = [];
    public List<DdlTriggerRow> DdlTriggers { get; set; } = [];
    public List<RowCountRow> RowCounts { get; set; } = [];
    public List<TemporalRow> Temporal { get; set; } = [];
    public List<DependencyRow> Dependencies { get; set; } = [];
    public List<ExternalReferenceRow> ExternalReferences { get; set; } = [];
    public List<ExtendedPropertyRow> ExtendedProperties { get; set; } = [];
    public List<RoleRow> Roles { get; set; } = [];
    public List<RoleMemberRow> RoleMembers { get; set; } = [];
    public List<UserRow> Users { get; set; } = [];
    public List<ParameterRow> Parameters { get; set; } = [];
    public List<PlanGuideRow> PlanGuides { get; set; } = [];
    public List<DatabaseScopedConfigurationRow> DatabaseScopedConfigurations { get; set; } = [];
    public List<LegacyRuleDefaultRow> LegacyRuleDefaults { get; set; } = [];
    public List<PermissionRow> Permissions { get; set; } = [];
    public List<UserDefinedTypeRow> UserDefinedTypes { get; set; } = [];
    public List<TableTypeRow> TableTypes { get; set; } = [];
    public List<TableTypeColumnRow> TableTypeColumns { get; set; } = [];
    public List<TableTypeIndexRow> TableTypeIndexes { get; set; } = [];
    public List<TableTypeDependentRow> TableTypeDependents { get; set; } = [];
    public List<IndexColumnRow> TableTypeIndexColumns { get; set; } = [];
    public List<TableTypeCheckRow> TableTypeChecks { get; set; } = [];
    public List<PartitionFunctionRow> PartitionFunctions { get; set; } = [];
    public List<PartitionRangeValueRow> PartitionRangeValues { get; set; } = [];
    public List<PartitionSchemeRow> PartitionSchemes { get; set; } = [];
    public List<PartitionSchemeFileRow> PartitionSchemeFiles { get; set; } = [];

    public string ServerName { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public string ProductVersion { get; set; } = "";
    public bool HasViewDefinition { get; set; }
}

/// <summary>
/// Tüm şema metadata'sını obje başına değil, obje SINIFI başına tek sorguyla çeker.
/// 10 sorgu paralel bağlantılarda koşar; DacFx'in semantik model kurmasına gerek yok.
/// </summary>
public sealed class CatalogExtractor(ExtractionGate? gate = null, int commandTimeoutSeconds = 300)
{
    private readonly ExtractionGate _gate = gate ?? ExtractionGate.Unbounded;

    internal async Task<(CatalogSet Catalog, ExtractionReport Report)> ExtractAsync(
        string connectionString, CancellationToken ct = default)
    {
        var report = new ExtractionReport();
        var catalog = new CatalogSet();
        var total = Stopwatch.StartNew();

        await PreflightAsync(connectionString, catalog, report, ct);

        // Her sorgu kendi bağlantısında — 10 sorgu paralel koşar, toplam süre en yavaş sorgu kadardır.
        var tasks = new List<Task>
        {
            Run("schemas", Sql.Schemas, r => new SchemaRow(Rdr.Int(r, 0), Rdr.Str(r, 1)), rows => catalog.Schemas = rows),
            Run("objects", Sql.Objects, MapObject, rows => catalog.Objects = rows),
            Run("modules", Sql.Modules, MapModule, rows => catalog.Modules = rows),
            Run("columns", Sql.Columns, MapColumn, rows => catalog.Columns = rows),
            Run("indexes", Sql.Indexes, MapIndex, rows => catalog.Indexes = rows),
            Run("indexColumns", Sql.IndexColumns, MapIndexColumn, rows => catalog.IndexColumns = rows),
            Run("keyConstraints", Sql.KeyConstraints, MapKeyConstraint, rows => catalog.KeyConstraints = rows),
            Run("foreignKeys", Sql.ForeignKeys, MapForeignKey, rows => catalog.ForeignKeys = rows),
            Run("foreignKeyColumns", Sql.ForeignKeyColumns, MapForeignKeyColumn, rows => catalog.ForeignKeyColumns = rows),
            Run("checkConstraints", Sql.CheckConstraints, MapCheckConstraint, rows => catalog.CheckConstraints = rows),
            Run("synonyms", Sql.Synonyms, MapSynonym, rows => catalog.Synonyms = rows),
            Run("sequences", Sql.Sequences, MapSequence, rows => catalog.Sequences = rows),
            Run("triggers", Sql.Triggers, MapTrigger, rows => catalog.Triggers = rows),

            // Yetki gerektirir; olmadan da karşılaştırma çalışır, yalnızca risk analizi zayıflar.
            Run("rowCounts", Sql.RowCounts, MapRowCount, rows => catalog.RowCounts = rows, optional: true),

            // sys.periods eski sürümlerde yok; eksik kalması karşılaştırmayı durdurmamalı.
            Run("temporal", Sql.Temporal, MapTemporal, rows => catalog.Temporal = rows, optional: true),
            Run("ddlTriggers", Sql.DdlTriggers, MapDdlTrigger, rows => catalog.DdlTriggers = rows, optional: true),

            // Tipli XML kolonlarının koleksiyon adı; yoksa kolon düz "xml" script'lenirdi.
            Run("xmlSchemaCollections", Sql.XmlSchemaCollections, MapXmlSchemaCollection,
                rows => catalog.XmlSchemaCollections = rows, optional: true),

            // XML schema collection içeriği: XML_SCHEMA_NAMESPACE kolon argümanıyla
            // çalışmazsa düşer; ad + namespace karşılaştırması yine de sürer.
            Run("xmlSchemaNamespaces", Sql.XmlSchemaNamespaces, MapXmlSchemaNamespace,
                rows => catalog.XmlSchemaNamespaces = rows, optional: true),
            Run("xmlSchemaContent", Sql.XmlSchemaCollectionContent, MapXmlSchemaContent,
                rows => catalog.XmlSchemaContents = rows, optional: true),

            // optimize_for_sequential_key SQL 2019+; eski sunucuda bu sorgu düşer,
            // karşılaştırma yalnız bu iki ayar olmadan sürer.
            Run("indexExtras", Sql.IndexExtras, MapIndexExtra, rows => catalog.IndexExtras = rows, optional: true),

            // XML / spatial index'ler: genel index yolundan ayrı, kendi sözdizimleri var.
            Run("xmlIndexes", Sql.XmlIndexes, MapXmlIndex, rows => catalog.XmlIndexes = rows, optional: true),
            Run("spatialIndexes", Sql.SpatialIndexes, MapSpatialIndex,
                rows => catalog.SpatialIndexes = rows, optional: true),

            // Full-text: sunucuda FTS kurulu değilse ya da yetki yoksa düşebilir.
            Run("fullTextStoplists", Sql.FullTextStoplists, MapFullTextStoplist,
                rows => catalog.FullTextStoplists = rows, optional: true),
            Run("fullTextStopwords", Sql.FullTextStopwords, MapFullTextStopword,
                rows => catalog.FullTextStopwords = rows, optional: true),
            Run("fullTextCatalogs", Sql.FullTextCatalogs, MapFullTextCatalog,
                rows => catalog.FullTextCatalogs = rows, optional: true),
            Run("fullTextIndexes", Sql.FullTextIndexes, MapFullTextIndex,
                rows => catalog.FullTextIndexes = rows, optional: true),
            Run("fullTextIndexColumns", Sql.FullTextIndexColumns, MapFullTextIndexColumn,
                rows => catalog.FullTextIndexColumns = rows, optional: true),

            // sys.stats.is_incremental SQL Server 2014 ile geldi; eski sürümde sorgu düşer,
            // karşılaştırma istatistiksiz devam eder (rapora uyarı düşülür).
            Run("statistics", Sql.Statistics, MapStatistic, rows => catalog.Statistics = rows, optional: true),
            Run("statisticColumns", Sql.StatisticColumns, MapStatisticColumn,
                rows => catalog.StatisticColumns = rows, optional: true),

            Run("externalReferences", Sql.ExternalReferences, MapExternalReference,
                rows => catalog.ExternalReferences = rows, optional: true),

            // Yalnızca script üretiminde kullanılır; karşılaştırmayı etkilemez.
            Run("dependencies", Sql.Dependencies, MapDependency, rows => catalog.Dependencies = rows, optional: true),

            // Extended property'ler eski/kısıtlı sürümlerde ya da yetki eksikliğinde
            // sorun çıkarabilir; eksik kalması karşılaştırmayı durdurmamalı.
            Run("extendedProperties", Sql.ExtendedProperties, MapExtendedProperty,
                rows => catalog.ExtendedProperties = rows, optional: true),

            // Güvenlik (rol/izin) yetki gerektirebilir; eksik kalması karşılaştırmayı durdurmamalı.
            Run("roles", Sql.Roles, MapRole, rows => catalog.Roles = rows, optional: true),
            Run("roleMembers", Sql.RoleMembers, MapRoleMember, rows => catalog.RoleMembers = rows, optional: true),
            Run("users", Sql.Users, MapUser, rows => catalog.Users = rows, optional: true),
            Run("parameters", Sql.Parameters, MapParameter, rows => catalog.Parameters = rows, optional: true),

            // Plan guide / DB scoped configuration / legacy RULE-DEFAULT: hepsi opsiyonel.
            // sys.database_scoped_configurations SQL Server 2016 ile geldi.
            Run("planGuides", Sql.PlanGuides, MapPlanGuide, rows => catalog.PlanGuides = rows, optional: true),
            Run("databaseScopedConfigurations", Sql.DatabaseScopedConfigurations, MapDatabaseScopedConfiguration,
                rows => catalog.DatabaseScopedConfigurations = rows, optional: true),
            Run("legacyRuleDefaults", Sql.LegacyRuleDefaults, MapLegacyRuleDefault,
                rows => catalog.LegacyRuleDefaults = rows, optional: true),
            Run("permissions", Sql.Permissions, MapPermission, rows => catalog.Permissions = rows, optional: true),
            Run("userDefinedTypes", Sql.UserDefinedTypes, MapUserDefinedType,
                rows => catalog.UserDefinedTypes = rows, optional: true),
            Run("tableTypes", Sql.TableTypes, MapTableType, rows => catalog.TableTypes = rows, optional: true),
            Run("tableTypeColumns", Sql.TableTypeColumns, MapTableTypeColumn,
                rows => catalog.TableTypeColumns = rows, optional: true),
            Run("tableTypeDependents", Sql.TableTypeDependents, MapTableTypeDependent,
                rows => catalog.TableTypeDependents = rows, optional: true),
            Run("tableTypeIndexes", Sql.TableTypeIndexes, MapTableTypeIndex,
                rows => catalog.TableTypeIndexes = rows, optional: true),
            Run("tableTypeIndexColumns", Sql.TableTypeIndexColumns, MapIndexColumn,
                rows => catalog.TableTypeIndexColumns = rows, optional: true),
            Run("tableTypeChecks", Sql.TableTypeChecks, MapTableTypeCheck,
                rows => catalog.TableTypeChecks = rows, optional: true),
            Run("partitionFunctions", Sql.PartitionFunctions, MapPartitionFunction,
                rows => catalog.PartitionFunctions = rows, optional: true),
            Run("partitionRangeValues", Sql.PartitionRangeValues, MapPartitionRangeValue,
                rows => catalog.PartitionRangeValues = rows, optional: true),
            Run("partitionSchemes", Sql.PartitionSchemes, MapPartitionScheme,
                rows => catalog.PartitionSchemes = rows, optional: true),
            Run("partitionSchemeFiles", Sql.PartitionSchemeFiles, MapPartitionSchemeFile,
                rows => catalog.PartitionSchemeFiles = rows, optional: true),
        };

        await Task.WhenAll(tasks);
        report.TotalExtraction = total.Elapsed;

        VerifyDefinitionsVisible(catalog, report);
        return (catalog, report);

        Task Run<T>(string name, string sql, Func<SqlDataReader, T> map, Action<List<T>> assign,
                    bool optional = false) =>
            Task.Run(async () =>
            {
                try
                {
                    var (rows, elapsed) = await _gate.RunAsync(
                        () => QueryAsync(connectionString, sql, map, ct), ct);
                    assign(rows);
                    lock (report)
                    {
                        report.QueryTimings[name] = elapsed;
                        report.RowCounts[name] = rows.Count;
                    }
                }
                catch (Exception ex) when (optional)
                {
                    lock (report)
                    {
                        report.FailedQueries.Add(name);
                        report.Warnings.Add(
                            $"'{name}' sorgusu çalıştırılamadı ({ex.Message.Split('\n')[0]}). " +
                            "Karşılaştırma sürüyor, ancak bu veriye dayanan analizler eksik kalacak.");
                    }
                }
            }, ct);
    }

    private async Task PreflightAsync(
        string connectionString, CatalogSet catalog, ExtractionReport report, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(Sql.Preflight, conn) { CommandTimeout = commandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            catalog.ServerName = reader.IsDBNull(0) ? conn.DataSource : reader.GetString(0);
            catalog.DatabaseName = reader.GetString(1);
            catalog.ProductVersion = reader.IsDBNull(2) ? "?" : Convert.ToString(reader.GetValue(2)) ?? "?";
            catalog.HasViewDefinition = !reader.IsDBNull(3) && reader.GetInt32(3) == 1;
        }
        report.QueryTimings["preflight"] = sw.Elapsed;
    }

    /// <summary>
    /// VIEW DEFINITION yetkisi yoksa sys.sql_modules.definition sessizce NULL döner ve
    /// tüm modüller "aynı" görünür. Bu, aracın verebileceği en tehlikeli yanlış cevap.
    /// </summary>
    private static void VerifyDefinitionsVisible(CatalogSet catalog, ExtractionReport report)
    {
        if (catalog.Modules.Count == 0) return;

        var nullDefinitions = catalog.Modules.Count(m => m.Definition is null);
        if (nullDefinitions == catalog.Modules.Count)
        {
            report.Warnings.Add(
                $"KRİTİK: {catalog.Modules.Count} modülün tamamında definition NULL. " +
                "VIEW DEFINITION yetkisi yok — karşılaştırma sonucu güvenilir değil.");
        }
        else if (nullDefinitions > 0)
        {
            report.Warnings.Add(
                $"UYARI: {nullDefinitions}/{catalog.Modules.Count} modülün definition alanı NULL " +
                "(şifrelenmiş obje ya da kısmi yetki). Bu objeler karşılaştırma dışı sayılmalı.");
        }

        if (!catalog.HasViewDefinition)
        {
            report.Warnings.Add("Bağlanan kullanıcıda veritabanı seviyesinde VIEW DEFINITION yetkisi görünmüyor.");
        }
    }

    private async Task<(List<T> Rows, TimeSpan Elapsed)> QueryAsync<T>(
        string connectionString, string sql, Func<SqlDataReader, T> map, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var rows = new List<T>(capacity: 1024);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = commandTimeoutSeconds };
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        while (await reader.ReadAsync(ct)) rows.Add(map(reader));

        return (rows, sw.Elapsed);
    }

    // --- Mapper'lar: ordinaller kesinlikle artan sırada okunmalı (SequentialAccess) ---

    private static ObjectRow MapObject(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3), Rdr.Date(r, 4), Rdr.Int(r, 5));

    private static ModuleRow MapModule(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NStr(r, 1), Rdr.Bool(r, 2), Rdr.Bool(r, 3));

    private static ColumnRow MapColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2),
        Rdr.Str(r, 3), Rdr.Str(r, 4),
        Rdr.Short(r, 5), Rdr.Byte(r, 6), Rdr.Byte(r, 7),
        Rdr.Bool(r, 8), Rdr.NStr(r, 9), Rdr.Bool(r, 10), Rdr.Bool(r, 11),
        Rdr.NStr(r, 12), Rdr.NStr(r, 13), Rdr.NBool(r, 14),
        Rdr.NStr(r, 15), Rdr.NBool(r, 16),
        Rdr.Variant(r, 17), Rdr.Variant(r, 18),
        Rdr.Byte(r, 19), Rdr.Bool(r, 20),
        Rdr.Bool(r, 21), Rdr.Bool(r, 22), Rdr.Bool(r, 23), Rdr.Bool(r, 24),
        Rdr.Int(r, 25), Rdr.Bool(r, 26), Rdr.NBool(r, 27) == true);

    private static XmlSchemaCollectionRow MapXmlSchemaCollection(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NStr(r, 1) ?? "dbo", Rdr.Str(r, 2));

    private static IndexRow MapIndex(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.NStr(r, 2), Rdr.Str(r, 3),
        Rdr.Bool(r, 4), Rdr.Bool(r, 5), Rdr.Bool(r, 6),
        Rdr.Byte(r, 7), Rdr.Bool(r, 8), Rdr.Bool(r, 9), Rdr.NStr(r, 10), Rdr.NStr(r, 11),
        Rdr.Bool(r, 12), Rdr.Bool(r, 13));

    private static IndexColumnRow MapIndexColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Int(r, 2), Rdr.Int(r, 3),
        Rdr.Byte(r, 4), Rdr.Bool(r, 5), Rdr.Bool(r, 6));

    private static XmlSchemaNamespaceRow MapXmlSchemaNamespace(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1));

    private static XmlSchemaContentRow MapXmlSchemaContent(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NStr(r, 1));

    private static IndexExtraRow MapIndexExtra(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.NBool(r, 2) == true, Rdr.NBool(r, 3) == true);

    private static XmlIndexRow MapXmlIndex(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2), Rdr.NInt(r, 3), Rdr.NStr(r, 4), Rdr.NStr(r, 5));

    private static SpatialIndexRow MapSpatialIndex(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3),
        Rdr.NDbl(r, 4), Rdr.NDbl(r, 5), Rdr.NDbl(r, 6), Rdr.NDbl(r, 7),
        Rdr.NStr(r, 8), Rdr.NStr(r, 9), Rdr.NStr(r, 10), Rdr.NStr(r, 11), Rdr.NInt(r, 12));

    private static FullTextStoplistRow MapFullTextStoplist(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1));

    private static FullTextStopwordRow MapFullTextStopword(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Int(r, 2));

    private static FullTextCatalogRow MapFullTextCatalog(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Bool(r, 2), Rdr.Bool(r, 3));

    private static FullTextIndexRow MapFullTextIndex(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NStr(r, 1), Rdr.NStr(r, 2), Rdr.Bool(r, 3),
        Rdr.NStr(r, 4), Rdr.NInt(r, 5), Rdr.NStr(r, 6));

    private static FullTextIndexColumnRow MapFullTextIndexColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Int(r, 2), Rdr.Int(r, 3));

    private static StatisticRow MapStatistic(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2), Rdr.Bool(r, 3), Rdr.NStr(r, 4), Rdr.Bool(r, 5));

    private static StatisticColumnRow MapStatisticColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Int(r, 2), Rdr.Int(r, 3));

    private static KeyConstraintRow MapKeyConstraint(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NInt(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3), Rdr.Bool(r, 4));

    private static ForeignKeyRow MapForeignKey(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2), Rdr.Bool(r, 3), Rdr.Int(r, 4),
        Rdr.Byte(r, 5), Rdr.Byte(r, 6), Rdr.Bool(r, 7), Rdr.Bool(r, 8), Rdr.Bool(r, 9));

    private static ForeignKeyColumnRow MapForeignKeyColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Int(r, 2), Rdr.Int(r, 3), Rdr.Int(r, 4), Rdr.Int(r, 5));

    private static CheckConstraintRow MapCheckConstraint(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Bool(r, 2), Rdr.NStr(r, 3), Rdr.Bool(r, 4), Rdr.Bool(r, 5),
        Rdr.Bool(r, 6));

    private static SynonymRow MapSynonym(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1));

    private static SequenceRow MapSequence(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Byte(r, 2), Rdr.Byte(r, 3),
        Rdr.Variant(r, 4), Rdr.Variant(r, 5), Rdr.Variant(r, 6), Rdr.Variant(r, 7),
        Rdr.Bool(r, 8), Rdr.Bool(r, 9), Rdr.NInt(r, 10));

    private static TriggerRow MapTrigger(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Bool(r, 1), Rdr.Bool(r, 2));

    private static RowCountRow MapRowCount(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Long(r, 1));

    private static TemporalRow MapTemporal(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NStr(r, 1), Rdr.NStr(r, 2), Rdr.NStr(r, 3), Rdr.NStr(r, 4));

    private static DdlTriggerRow MapDdlTrigger(SqlDataReader r) => new(
        Rdr.Str(r, 0), Rdr.Bool(r, 1), Rdr.NStr(r, 2), Rdr.Bool(r, 3), Rdr.Bool(r, 4));

    private static ExternalReferenceRow MapExternalReference(SqlDataReader r) => new(
        Rdr.Str(r, 0), Rdr.Str(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3), Rdr.Str(r, 4), Rdr.Str(r, 5));

    private static DependencyRow MapDependency(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1));

    private static ExtendedPropertyRow MapExtendedProperty(SqlDataReader r) => new(
        Rdr.Byte(r, 0), Rdr.Int(r, 1), Rdr.Int(r, 2), Rdr.Str(r, 3), Rdr.NStr(r, 4));

    private static RoleRow MapRole(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.NStr(r, 2), Rdr.Bool(r, 3));

    private static RoleMemberRow MapRoleMember(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.NStr(r, 1));

    private static UserRow MapUser(SqlDataReader r) => new(
        Rdr.Str(r, 0), Rdr.Str(r, 1).Trim(), Rdr.NStr(r, 2), Rdr.Int(r, 3));

    private static PlanGuideRow MapPlanGuide(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Bool(r, 2), Rdr.Str(r, 3),
        Rdr.NStr(r, 4), Rdr.NStr(r, 5), Rdr.NStr(r, 6),
        Rdr.NStr(r, 7), Rdr.NStr(r, 8), Rdr.NStr(r, 9));

    private static DatabaseScopedConfigurationRow MapDatabaseScopedConfiguration(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.NStr(r, 2), Rdr.NStr(r, 3));

    private static LegacyRuleDefaultRow MapLegacyRuleDefault(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3), Rdr.NStr(r, 4));

    private static ParameterRow MapParameter(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2));

    private static PermissionRow MapPermission(SqlDataReader r) => new(
        Rdr.Byte(r, 0), Rdr.Int(r, 1), Rdr.Int(r, 2), Rdr.Str(r, 3), Rdr.Str(r, 4), Rdr.NStr(r, 5));

    private static UserDefinedTypeRow MapUserDefinedType(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3),
        Rdr.Short(r, 4), Rdr.Byte(r, 5), Rdr.Byte(r, 6), Rdr.Bool(r, 7), Rdr.NStr(r, 8));

    private static TableTypeRow MapTableType(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Str(r, 2), Rdr.Int(r, 3));

    private static TableTypeColumnRow MapTableTypeColumn(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2), Rdr.Str(r, 3), Rdr.Str(r, 4),
        Rdr.Short(r, 5), Rdr.Byte(r, 6), Rdr.Byte(r, 7), Rdr.Bool(r, 8), Rdr.NStr(r, 9),
        Rdr.Bool(r, 10), Rdr.Bool(r, 11), Rdr.NStr(r, 12));

    private static TableTypeDependentRow MapTableTypeDependent(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1));

    private static TableTypeIndexRow MapTableTypeIndex(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.NStr(r, 2), Rdr.Str(r, 3),
        Rdr.Bool(r, 4), Rdr.Bool(r, 5), Rdr.Bool(r, 6), Rdr.NStr(r, 7), Rdr.NBool(r, 8));

    private static TableTypeCheckRow MapTableTypeCheck(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Bool(r, 2), Rdr.NStr(r, 3));

    private static PartitionFunctionRow MapPartitionFunction(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Bool(r, 2), Rdr.NStr(r, 3));

    private static PartitionRangeValueRow MapPartitionRangeValue(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.NStr(r, 2));

    private static PartitionSchemeRow MapPartitionScheme(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Str(r, 1), Rdr.Str(r, 2));

    private static PartitionSchemeFileRow MapPartitionSchemeFile(SqlDataReader r) => new(
        Rdr.Int(r, 0), Rdr.Int(r, 1), Rdr.Str(r, 2));
}
