using Microsoft.Data.SqlClient;
using SchemaDiff.Core;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Canlı SQL Server'a karşı uçtan uca regresyon. Büyük fixture, sınıfa özel geçici
/// veritabanlarında üretilir ve testler bitince silinir; demo DB'lerinin durumuna bağlı değildir.
/// Yerel SQL Server yoksa büyük testler, küçük fixture yoksa küçük test atlanır.
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests(LargeDatabaseFixture fixture) : IClassFixture<LargeDatabaseFixture>
{
    private static string Conn(string db) =>
        $"Server=localhost;Database={db};Integrated Security=true;TrustServerCertificate=true;Encrypt=false;Connect Timeout=5";

    private static bool DbAvailable(string db)
    {
        try
        {
            using var c = new SqlConnection(Conn(db));
            c.Open();
            return true;
        }
        catch { return false; }
    }

    private static int Count(string text, string pattern) =>
        System.Text.RegularExpressions.Regex.Matches(text, System.Text.RegularExpressions.Regex.Escape(pattern)).Count;

    [SkippableFact]
    public async Task Small_fixture_yields_expected_five_differences()
    {
        Skip.IfNot(DbAvailable("SchemaDiff_Dev") && DbAvailable("SchemaDiff_Prod"), "Fixture DB'leri yok.");

        var result = await SchemaDiffService.CompareAsync(Conn("SchemaDiff_Dev"), Conn("SchemaDiff_Prod"));

        Assert.Equal(2, result.AddedCount);
        Assert.Equal(1, result.RemovedCount);
        Assert.Equal(2, result.ChangedCount);
    }

    [SkippableFact]
    public async Task Big_fixture_yields_expected_difference_count()
    {
        Skip.IfNot(fixture.UnavailableReason is null, fixture.UnavailableReason ?? "SQL Server kullanılamıyor.");

        var result = await SchemaDiffService.CompareAsync(fixture.SourceConnection, fixture.TargetConnection);

        // Big2, generate-large.sql'de Drift=1 ile üretildi — kontrollü, sabit fark kümesi.
        Assert.Equal(359, result.Differences.Count);
    }

    [SkippableFact]
    public async Task Big_script_matches_ssdt_operation_counts()
    {
        Skip.IfNot(fixture.UnavailableReason is null, fixture.UnavailableReason ?? "SQL Server kullanılamıyor.");

        var options = SnapshotOptions.Default with { KeepDisplayScripts = true };
        var result = await SchemaDiffService.CompareAsync(fixture.SourceConnection, fixture.TargetConnection, options);

        var module = ModuleScriptGenerator.Generate(result, selection: null, ScriptOptions.Default);
        var table = TableScriptGenerator.Generate(result, selection: null, new TableScriptOptions { AllowDataLoss = true });
        var sql = table.Sql + "\n" + module.Sql;

        // SSDT'nin SchemaDiff_Big2_Update1.publish.sql çıktısıyla birebir eşleşen kategoriler
        // (elle doğrulanmıştı; burada regresyona karşı kilitlenir).
        Assert.Equal(125, Count(sql, "ALTER PROCEDURE"));
        Assert.Equal(25, Count(sql, "DROP PROCEDURE"));
        Assert.Equal(10, Count(sql, "CREATE TABLE"));
        Assert.Equal(29, Count(sql, "DROP INDEX"));
    }
}
