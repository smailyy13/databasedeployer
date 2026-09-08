using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SchemaDiff.Core.Connections;

namespace SchemaDiff.Web;

public sealed class StoredConnection
{
    public required string Id { get; set; }
    public required string Server { get; set; }
    public string? Database { get; set; }
    public SqlAuthentication Authentication { get; set; }
    public string? UserName { get; set; }
    public bool Encrypt { get; set; }
    public bool TrustServerCertificate { get; set; } = true;
    public DateTimeOffset LastUsed { get; set; }

    /// <summary>DPAPI ile kullanıcıya özel şifrelenmiş parola (base64). Asla düz metin.</summary>
    public string? ProtectedPassword { get; set; }

    public bool HasStoredPassword => !string.IsNullOrEmpty(ProtectedPassword);
}

/// <summary>
/// Son kullanılan bağlantılar — SSDT'deki "Recent Connections" listesinin karşılığı.
///
/// Parola yalnızca kullanıcı "Parolayı hatırla" derse ve yalnızca Windows DPAPI ile,
/// o kullanıcı hesabına bağlı olarak saklanır. Başka bir hesap dosyayı okusa bile
/// çözemez. Parola hiçbir koşulda tarayıcıya geri gönderilmez.
/// </summary>
public sealed class RecentConnections
{
    private const int MaxEntries = 20;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SchemaDiff.RecentConnections.v1");

    private readonly string _path;
    private readonly object _gate = new();
    private List<StoredConnection> _entries = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public RecentConnections()
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SchemaDiff");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "recent.json");
        Load();
    }

    public IReadOnlyList<StoredConnection> All
    {
        get { lock (_gate) return [.. _entries.OrderByDescending(e => e.LastUsed)]; }
    }

    public void Remember(SqlConnectionInfo info, bool rememberPassword)
    {
        var id = MakeId(info.Key);

        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                entry = new StoredConnection { Id = id, Server = info.Server };
                _entries.Add(entry);
            }

            entry.Server = info.Server;
            entry.Database = info.Database;
            entry.Authentication = info.Authentication;
            entry.UserName = info.UserName;
            entry.Encrypt = info.Encrypt;
            entry.TrustServerCertificate = info.TrustServerCertificate;
            entry.LastUsed = DateTimeOffset.UtcNow;

            if (rememberPassword && info.Authentication == SqlAuthentication.SqlLogin
                && !string.IsNullOrEmpty(info.Password))
            {
                entry.ProtectedPassword = Protect(info.Password);
            }
            else if (!rememberPassword)
            {
                entry.ProtectedPassword = null;
            }

            _entries = [.. _entries.OrderByDescending(e => e.LastUsed).Take(MaxEntries)];
            Save();
        }
    }

    /// <summary>Kullanıcı kayıtlı bir bağlantıyı seçtiğinde parolayı sunucu tarafında çözer.</summary>
    public string? ResolvePassword(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        lock (_gate)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            return entry?.ProtectedPassword is null ? null : Unprotect(entry.ProtectedPassword);
        }
    }

    public void Forget(string id)
    {
        lock (_gate)
        {
            _entries.RemoveAll(e => e.Id == id);
            Save();
        }
    }

    // --- kalıcılık ---

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            _entries = JsonSerializer.Deserialize<List<StoredConnection>>(
                File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (Exception)
        {
            // Bozuk dosya uygulamayı düşürmemeli; liste boş başlar ve ilk kayıtta düzelir.
            _entries = [];
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (IOException)
        {
            // Kayıt edememek karşılaştırmayı engellememeli.
        }
    }

    // --- parola koruma ---

    private static string? Protect(string password)
    {
        // DPAPI yalnızca Windows'ta var. Başka platformda parolayı düz metin
        // yazmaktansa hiç saklamamak doğru davranış.
        if (!OperatingSystem.IsWindows()) return null;

        var bytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(password), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string protectedValue)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var bytes = ProtectedData.Unprotect(
                Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException)
        {
            // Başka bir kullanıcı hesabıyla şifrelenmiş; çözülemez.
            return null;
        }
    }

    private static string MakeId(string key) =>
        Convert.ToHexString(System.IO.Hashing.XxHash128.Hash(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
}
