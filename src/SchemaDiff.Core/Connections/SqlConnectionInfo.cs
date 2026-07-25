using Microsoft.Data.SqlClient;

namespace SchemaDiff.Core.Connections;

public enum SqlAuthentication
{
    Windows,
    SqlLogin,
}

/// <summary>
/// Bağlantının alanlara ayrılmış hâli. Kullanıcıya ham bağlantı dizesi yazdırmak
/// yerine SSDT'deki Connect diyaloğuyla aynı alanlar sorulur; dize burada kurulur.
/// </summary>
public sealed record SqlConnectionInfo
{
    public required string Server { get; init; }
    public string? Database { get; init; }
    public SqlAuthentication Authentication { get; init; } = SqlAuthentication.Windows;
    public string? UserName { get; init; }
    public string? Password { get; init; }
    public bool Encrypt { get; init; }
    public bool TrustServerCertificate { get; init; } = true;
    public int ConnectTimeoutSeconds { get; init; } = 15;

    public string Label => string.IsNullOrWhiteSpace(Database) ? Server : $"{Server}.{Database}";

    /// <summary>Parolayı ASLA içermeyen, kayıt ve ekran için güvenli anahtar.</summary>
    public string Key =>
        $"{Server}|{Database}|{Authentication}|{(Authentication == SqlAuthentication.SqlLogin ? UserName : "")}";

    public string ToConnectionString(string? overrideDatabase = null)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            // SSDT'deki "Encrypt: Optional (False)" ile aynı anlam.
            Encrypt = Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "SchemaDiff",
        };

        var database = overrideDatabase ?? Database;
        if (!string.IsNullOrWhiteSpace(database)) builder.InitialCatalog = database;

        if (Authentication == SqlAuthentication.Windows)
        {
            builder.IntegratedSecurity = true;
        }
        else
        {
            builder.UserID = UserName ?? string.Empty;
            builder.Password = Password ?? string.Empty;
        }

        return builder.ConnectionString;
    }
}
