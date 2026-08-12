using System.Diagnostics;
using Microsoft.Data.SqlClient;

namespace SchemaDiff.Core.Analysis;

public sealed record CoverageItem(
    string Category, string Item, bool Covered, long? Count, string? Error);

public sealed record CoverageReport(
    string Server, string Database, string ProductVersion,
    IReadOnlyList<CoverageItem> Items, TimeSpan Elapsed)
{
    /// <summary>Kapsanmayan ama veritabanında GERÇEKTEN bulunan sınıflar — yapılacaklar listesi budur.</summary>
    public IReadOnlyList<CoverageItem> Gaps =>
        [.. Items.Where(i => !i.Covered && i.Count > 0).OrderByDescending(i => i.Count)];
}

/// <summary>
/// "Bu veritabanında neyi kaçırıyoruz?" sorusunu ölçer.
///
/// Karşılaştırma motoru kapsamadığı obje sınıflarını sessizce atlar. Bu, aracın
/// verebileceği en tehlikeli yanlış cevaptır: rapor temiz görünür ama eksiktir.
/// Bu sonda her sınıfın veritabanında kaç tane bulunduğunu sayar; böylece kapsam
/// eksiği tahminle değil sayıyla bilinir.
///
/// Her sorgu ayrı ayrı ve hataya dayanıklı koşar — bir katalog view'ı o SQL Server
/// sürümünde yoksa ya da yetki yetmiyorsa rapor yine tamamlanır.
/// </summary>
public static class CoverageProbe
{
    internal static readonly (string Category, string Item, bool Covered, string Sql)[] Probes =
    [
        // --- kapsananlar ---
        ("Şema", "Şemalar", true, "SELECT COUNT(*) FROM sys.schemas WHERE schema_id > 4 AND schema_id < 16384"),
        ("Tablo", "Tablolar", true, "SELECT COUNT(*) FROM sys.tables WHERE is_ms_shipped = 0"),
        ("Tablo", "Kolonlar", true, "SELECT COUNT(*) FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id WHERE o.is_ms_shipped = 0"),
        ("Tablo", "Index'ler", true, "SELECT COUNT(*) FROM sys.indexes i JOIN sys.objects o ON o.object_id = i.object_id WHERE o.is_ms_shipped = 0 AND i.type <> 0"),
        ("Tablo", "Foreign key'ler", true, "SELECT COUNT(*) FROM sys.foreign_keys WHERE is_ms_shipped = 0"),
        ("Tablo", "Check constraint'ler", true, "SELECT COUNT(*) FROM sys.check_constraints WHERE is_ms_shipped = 0"),
        ("Modül", "View'lar", true, "SELECT COUNT(*) FROM sys.views WHERE is_ms_shipped = 0"),
        ("Modül", "Prosedürler", true, "SELECT COUNT(*) FROM sys.procedures WHERE is_ms_shipped = 0"),
        ("Modül", "Fonksiyonlar", true, "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('FN','IF','TF')"),
        ("Modül", "DML trigger'ları", true, "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 1 AND is_ms_shipped = 0"),
        ("Diğer", "Sequence'lar", true, "SELECT COUNT(*) FROM sys.sequences WHERE is_ms_shipped = 0"),
        ("Diğer", "Synonym'ler", true, "SELECT COUNT(*) FROM sys.synonyms WHERE is_ms_shipped = 0"),
        ("Güvenlik", "İzinler (obje/şema, GRANT/DENY)", true, "SELECT COUNT(*) FROM sys.database_permissions WHERE class IN (1, 3)"),
        ("Güvenlik", "Kullanıcı tanımlı roller", true, "SELECT COUNT(*) FROM sys.database_principals WHERE type = 'R' AND is_fixed_role = 0 AND principal_id > 4 AND name <> 'public'"),
        ("Güvenlik", "Rol üyelikleri", true, "SELECT COUNT(*) FROM sys.database_role_members"),

        ("Güvenlik", "Veritabanı seviyesi izinler", true, "SELECT COUNT(*) FROM sys.database_permissions WHERE class = 0"),

        // --- kapsanmayanlar: sayısı > 0 çıkanlar yapılacaklar listesidir ---
        ("Güvenlik", "Veritabanı kullanıcıları", true, "SELECT COUNT(*) FROM sys.database_principals WHERE principal_id > 4 AND type IN ('S','U','G','E','X')"),
        ("Metadata", "Extended property'ler (obje/kolon/şema/db)", true, "SELECT COUNT(*) FROM sys.extended_properties WHERE class IN (0, 1, 3)"),
        ("Metadata", "Extended property'ler (parametre/principal vb.)", false, "SELECT COUNT(*) FROM sys.extended_properties WHERE class NOT IN (0, 1, 3)"),
        ("Tip", "Kullanıcı tanımlı alias tipler", true, "SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1 AND is_table_type = 0 AND is_assembly_type = 0"),
        ("Tip", "CLR (assembly) tipler", false, "SELECT COUNT(*) FROM sys.types WHERE is_user_defined = 1 AND is_assembly_type = 1"),
        ("Tip", "Table type'lar", true, "SELECT COUNT(*) FROM sys.table_types WHERE is_user_defined = 1"),
        ("Depolama", "Partition function'lar", true, "SELECT COUNT(*) FROM sys.partition_functions"),
        ("Depolama", "Partition scheme'ler", true, "SELECT COUNT(*) FROM sys.partition_schemes"),
        ("Depolama", "Filegroup'lar (PRIMARY dışı)", false, "SELECT COUNT(*) FROM sys.filegroups WHERE data_space_id > 1"),
        ("Index", "XML index'ler", true, "SELECT COUNT(*) FROM sys.xml_indexes"),
        ("Index", "Spatial index'ler", true, "SELECT COUNT(*) FROM sys.spatial_indexes"),
        ("Index", "Kullanıcı istatistikleri", true, "SELECT COUNT(*) FROM sys.stats WHERE user_created = 1"),
        ("Index", "Otomatik istatistikler (kapsam dışı — runtime artefaktı)", false, "SELECT COUNT(*) FROM sys.stats WHERE auto_created = 1"),
        ("Full-text", "Full-text katalogları", true, "SELECT COUNT(*) FROM sys.fulltext_catalogs"),
        ("Full-text", "Full-text index'ler", true, "SELECT COUNT(*) FROM sys.fulltext_indexes"),
        ("CLR", "Assembly'ler", false, "SELECT COUNT(*) FROM sys.assemblies WHERE is_user_defined = 1"),
        ("CLR", "CLR prosedür/fonksiyonları", false, "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('PC','FS','FT','AF')"),
        ("Tablo", "Temporal (system-versioned) tablolar", true, "SELECT COUNT(*) FROM sys.tables WHERE temporal_type = 2"),
        ("Tablo", "In-memory OLTP tablolar", false, "SELECT COUNT(*) FROM sys.tables WHERE is_memory_optimized = 1"),
        ("Tablo", "Graph node/edge tablolar", false, "SELECT COUNT(*) FROM sys.tables WHERE is_node = 1 OR is_edge = 1"),
        ("Güvenlik", "Always Encrypted kolonlar", false, "SELECT COUNT(*) FROM sys.columns WHERE encryption_type IS NOT NULL"),
        ("Trigger", "DDL trigger'ları", true, "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0"),
        ("Modül", "Şifrelenmiş modüller (okunamaz)", false, "SELECT COUNT(*) FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.is_ms_shipped = 0 AND m.definition IS NULL"),

        // --- yeni sayaçlar (Dalga 1: her boşluk artık ölçülüyor) ---
        ("Depolama", "Sıkıştırılmış partition'lar (DATA_COMPRESSION)", true, "SELECT COUNT(*) FROM sys.partitions p JOIN sys.objects o ON o.object_id = p.object_id WHERE o.is_ms_shipped = 0 AND p.data_compression <> 0"),
        ("Tablo", "Sparse / column set / FILESTREAM / ROWGUIDCOL kolonlar", true,"SELECT COUNT(*) FROM sys.columns c JOIN sys.objects o ON o.object_id = c.object_id WHERE o.is_ms_shipped = 0 AND (c.is_sparse = 1 OR c.is_column_set = 1 OR c.is_filestream = 1 OR c.is_rowguidcol = 1)"),
        ("Güvenlik", "Application role'ler", false, "SELECT COUNT(*) FROM sys.database_principals WHERE type = 'A'"),
        ("Güvenlik", "Row-Level Security (security policy)", false, "SELECT COUNT(*) FROM sys.security_policies"),
        ("Güvenlik", "Dynamic Data Masking kolonları", false, "SELECT COUNT(*) FROM sys.masked_columns"),
        ("Güvenlik", "Sertifikalar", false, "SELECT COUNT(*) FROM sys.certificates"),
        ("Güvenlik", "Simetrik anahtarlar", false, "SELECT COUNT(*) FROM sys.symmetric_keys WHERE name NOT LIKE '##%'"),
        ("Güvenlik", "Asimetrik anahtarlar", false, "SELECT COUNT(*) FROM sys.asymmetric_keys"),
        ("Güvenlik", "Database scoped credential'lar", false, "SELECT COUNT(*) FROM sys.database_scoped_credentials"),
        ("Güvenlik", "Column Master/Encryption Key (Always Encrypted)", false, "SELECT (SELECT COUNT(*) FROM sys.column_master_keys) + (SELECT COUNT(*) FROM sys.column_encryption_keys)"),
        ("Programlanabilirlik", "Service Broker (queue/service/contract)", false, "SELECT (SELECT COUNT(*) FROM sys.service_queues WHERE is_ms_shipped = 0) + (SELECT COUNT(*) FROM sys.services WHERE service_id > 5) + (SELECT COUNT(*) FROM sys.service_contracts WHERE service_contract_id > 5)"),
        ("Programlanabilirlik", "XML schema collection'lar", true, "SELECT COUNT(*) FROM sys.xml_schema_collections WHERE schema_id <> 4"),
        ("Programlanabilirlik", "Plan guide'lar", false, "SELECT COUNT(*) FROM sys.plan_guides"),
        ("Entegrasyon", "External data source/table (PolyBase)", false, "SELECT (SELECT COUNT(*) FROM sys.external_data_sources) + (SELECT COUNT(*) FROM sys.external_tables)"),
        ("Ayar", "Database scoped configuration'lar", false, "SELECT COUNT(*) FROM sys.database_scoped_configurations WHERE is_value_default = 0"),
        ("Legacy", "CREATE RULE / CREATE DEFAULT (bağlı objeler)", false, "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('R', 'D') AND parent_object_id = 0"),
    ];

    public static async Task<CoverageReport> RunAsync(string connectionString, CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        string server = connection.DataSource, database = connection.Database, version = "?";
        await using (var header = new SqlCommand(
            "SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')), DB_NAME(), CONVERT(nvarchar(64), SERVERPROPERTY('ProductVersion'))",
            connection))
        await using (var reader = await header.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                if (!reader.IsDBNull(0)) server = reader.GetString(0);
                database = reader.GetString(1);
                if (!reader.IsDBNull(2)) version = reader.GetString(2);
            }
        }

        var items = new List<CoverageItem>(Probes.Length);
        foreach (var (category, item, covered, sql) in Probes)
        {
            try
            {
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = 60 };
                var value = await command.ExecuteScalarAsync(ct);
                items.Add(new CoverageItem(category, item, covered, Convert.ToInt64(value), null));
            }
            catch (SqlException ex)
            {
                // Katalog view'ı bu sürümde yok ya da yetki yetmiyor — rapor devam etsin.
                items.Add(new CoverageItem(category, item, covered, null, ex.Message.Split('\n')[0].Trim()));
            }
        }

        return new CoverageReport(server, database, version, items, stopwatch.Elapsed);
    }
}
