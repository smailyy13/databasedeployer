using Microsoft.Data.SqlClient;

namespace SchemaDiff.Core.Connections;

public sealed record ServerProbe(
    bool Ok, string? Error, string? ServerName, string? Version, string? Edition, string? LoginName);

/// <summary>
/// Bağlantı diyaloğunun ihtiyaç duyduğu keşif işlemleri: bağlantıyı sına ve
/// sunucudaki veritabanlarını listele.
/// </summary>
public static class SqlServerExplorer
{
    public static async Task<ServerProbe> TestAsync(SqlConnectionInfo info, CancellationToken ct = default)
    {
        try
        {
            await using var connection = new SqlConnection(info.ToConnectionString());
            await connection.OpenAsync(ct);

            await using var command = new SqlCommand("""
                SELECT CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')),
                       CONVERT(nvarchar(128), SERVERPROPERTY('ProductVersion')),
                       CONVERT(nvarchar(256), SERVERPROPERTY('Edition')),
                       SUSER_SNAME();
                """, connection);

            await using var reader = await command.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return new ServerProbe(false, "Sunucu bilgisi okunamadı.", null, null, null, null);

            return new ServerProbe(
                true, null,
                reader.IsDBNull(0) ? info.Server : reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3));
        }
        catch (Exception ex)
        {
            return new ServerProbe(false, FirstLine(ex.Message), null, null, null, null);
        }
    }

    /// <summary>
    /// Sunucudaki kullanıcı veritabanları. Erişim yetkisi olmayanlar ve çevrimdışı
    /// olanlar listelenmez — seçilemeyecek bir adı göstermenin faydası yok.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListDatabasesAsync(
        SqlConnectionInfo info, CancellationToken ct = default)
    {
        var names = new List<string>();

        // master üzerinden sor: seçili veritabanı erişilemez olsa bile liste gelsin.
        await using var connection = new SqlConnection(info.ToConnectionString(overrideDatabase: "master"));
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand("""
            SELECT d.name
            FROM sys.databases AS d
            WHERE d.database_id > 4
              AND d.state = 0
              AND HAS_DBACCESS(d.name) = 1
            ORDER BY d.name;
            """, connection);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) names.Add(reader.GetString(0));

        return names;
    }

    private static string FirstLine(string message) => message.Split('\n')[0].Trim();
}
