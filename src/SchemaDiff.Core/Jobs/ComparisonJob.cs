using Microsoft.Data.SqlClient;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Jobs;

/// <summary>
/// Karşılaştırmanın atomik birimi: isimlendirilmiş bir kaynak/hedef çifti.
///
/// Kasıtlı olarak "aynı sunucuda aynı adlı veritabanları" varsayımı YOK.
/// İki taraf farklı sunucuda, farklı veritabanı adında olabilir; hangi çiftlerin
/// karşılaştırılacağı koda değil yapılandırmaya aittir.
/// </summary>
public sealed record ComparisonJob(string Name, string SourceConnectionString, string TargetConnectionString)
{
    /// <summary>Ekrana yazılabilir kaynak tanımı — bağlantı dizesindeki kimlik bilgileri sızmaz.</summary>
    public string SourceLabel => Describe(SourceConnectionString);

    public string TargetLabel => Describe(TargetConnectionString);

    /// <summary>Bir bağlantı dizesinin veritabanını değiştirerek yeni bir dize üretir.</summary>
    public static string WithDatabase(string connectionString, string database) =>
        new SqlConnectionStringBuilder(connectionString) { InitialCatalog = database }.ConnectionString;

    /// <summary>Yaygın durum için kısayol: iki sunucuda aynı adlı veritabanı.</summary>
    public static ComparisonJob ForDatabase(
        string sourceConnectionString, string targetConnectionString, string database, string? name = null) =>
        new(name ?? database,
            WithDatabase(sourceConnectionString, database),
            WithDatabase(targetConnectionString, database));

    /// <summary>
    /// Yalnızca sunucu ve veritabanı adını gösterir. Bağlantı dizesi parola içerebilir;
    /// hiçbir yerde ham hâliyle yazdırılmamalı.
    /// </summary>
    private static string Describe(string connectionString)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            var server = string.IsNullOrEmpty(builder.DataSource) ? "?" : builder.DataSource;
            var database = string.IsNullOrEmpty(builder.InitialCatalog) ? "(varsayılan)" : builder.InitialCatalog;
            return $"{server}.{database}";
        }
        catch (ArgumentException)
        {
            return "(geçersiz bağlantı dizesi)";
        }
    }
}

/// <summary>Tek bir işin sonucu — ya da neden yapılamadığı.</summary>
public sealed record JobResult(
    ComparisonJob Job,
    CompareResult? Comparison,
    string? Error,
    TimeSpan Duration)
{
    public bool Succeeded => Comparison is not null;

    public string Name => Job.Name;
}
