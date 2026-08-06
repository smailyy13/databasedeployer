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
    private static readonly (string Category, string Item, bool Covered, string Sql)[] Probes =
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
        ("Index", "XML index'ler", false, "SELECT COUNT(*) FROM sys.xml_indexes"),
        ("Index", "Spatial index'ler", false, "SELECT COUNT(*) FROM sys.spatial_indexes"),
        ("Index", "Kullanıcı istatistikleri", false, "SELECT COUNT(*) FROM sys.stats WHERE user_created = 1"),
        ("Full-text", "Full-text katalogları", false, "SELECT COUNT(*) FROM sys.fulltext_catalogs"),
        ("Full-text", "Full-text index'ler", false, "SELECT COUNT(*) FROM sys.fulltext_indexes"),
        ("CLR", "Assembly'ler", false, "SELECT COUNT(*) FROM sys.assemblies WHERE is_user_defined = 1"),
        ("CLR", "CLR prosedür/fonksiyonları", false, "SELECT COUNT(*) FROM sys.objects WHERE is_ms_shipped = 0 AND type IN ('PC','FS','FT','AF')"),
        ("Tablo", "Temporal (system-versioned) tablolar", true, "SELECT COUNT(*) FROM sys.tables WHERE temporal_type = 2"),
        ("Tablo", "In-memory OLTP tablolar", false, "SELECT COUNT(*) FROM sys.tables WHERE is_memory_optimized = 1"),
        ("Tablo", "Graph node/edge tablolar", false, "SELECT COUNT(*) FROM sys.tables WHERE is_node = 1 OR is_edge = 1"),
        ("Güvenlik", "Always Encrypted kolonlar", false, "SELECT COUNT(*) FROM sys.columns WHERE encryption_type IS NOT NULL"),
        ("Trigger", "DDL trigger'ları", true, "SELECT COUNT(*) FROM sys.triggers WHERE parent_class = 0 AND is_ms_shipped = 0"),
        ("Modül", "Şifrelenmiş modüller (okunamaz)", false, "SELECT COUNT(*) FROM sys.sql_modules m JOIN sys.objects o ON o.object_id = m.object_id WHERE o.is_ms_shipped = 0 AND m.definition IS NULL"),
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
