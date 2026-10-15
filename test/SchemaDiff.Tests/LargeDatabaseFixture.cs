using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;

namespace SchemaDiff.Tests;

/// <summary>
/// Owns a private drift-0/drift-1 pair for one test class. Demo databases may have
/// been deployed to already, so their current state cannot be a regression fixture.
/// Only databases successfully created by this instance are eligible for cleanup.
/// </summary>
public sealed class LargeDatabaseFixture : IAsyncLifetime
{
    private readonly string prefix = $"SchemaDiff_TestBig_{Guid.NewGuid():N}";
    private readonly List<string> created = [];
    public string? UnavailableReason { get; private set; }
    public string SourceConnection => Connection($"{prefix}_Source");
    public string TargetConnection => Connection($"{prefix}_Target");

    private static string Connection(string database) => new SqlConnectionStringBuilder
    {
        DataSource = "localhost",
        InitialCatalog = database,
        IntegratedSecurity = true,
        TrustServerCertificate = true,
        Encrypt = false,
        ConnectTimeout = 5,
        // Keep fixture-owned connections separate from any open application pools.
        ApplicationName = "SchemaDiff.IntegrationTests",
    }.ConnectionString;

    public async Task InitializeAsync()
    {
        await using var master = new SqlConnection(Connection("master"));
        try { await master.OpenAsync(); }
        catch (SqlException)
        {
            UnavailableReason = "Local SQL Server is unavailable; integration fixtures cannot be created.";
            return;
        }

        try
        {
            var script = await ReadGeneratorAsync();
            await CreateAsync(master, $"{prefix}_Source", script, drift: 0);
            await CreateAsync(master, $"{prefix}_Target", script, drift: 1);
        }
        catch
        {
            await DisposeAsync();
            throw; // Missing CREATE DATABASE permissions/setup errors must fail visibly.
        }
    }

    private static async Task<string> ReadGeneratorAsync()
    {
        await using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("SchemaDiff.Tests.GenerateLarge.sql")
            ?? throw new InvalidOperationException("Embedded large-database generator is missing.");
        using var reader = new StreamReader(stream);
        var script = await reader.ReadToEndAsync();
        // Never execute the generator's demo DROP/CREATE/USE preamble. Creation and
        // ownership are managed below, and SQL is run on an explicit test connection.
        var body = script.IndexOf("SET NOCOUNT ON;", StringComparison.Ordinal);
        if (body < 0) throw new InvalidOperationException("Large-database generator body was not found.");
        return script[body..]
            .Replace("$(Tables)", "3000", StringComparison.Ordinal)
            .Replace("$(Procs)", "5000", StringComparison.Ordinal)
            .Replace("$(Views)", "1500", StringComparison.Ordinal)
            .Replace("$(Funcs)", "500", StringComparison.Ordinal);
    }

    private async Task CreateAsync(SqlConnection master, string database, string script, int drift)
    {
        // A collision fails CREATE; an existing database is never dropped or reused.
        using (var create = new SqlCommand($"CREATE DATABASE [{database}];", master) { CommandTimeout = 120 })
            await create.ExecuteNonQueryAsync();
        created.Add(database);

        await using var connection = new SqlConnection(Connection(database));
        await connection.OpenAsync();
        script = script.Replace("$(Drift)", drift.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
        foreach (var batch in Regex.Split(script, @"^\s*GO\s*\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            using var command = new SqlCommand(batch, connection) { CommandTimeout = 180 };
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (created.Count == 0) return;
        await using var master = new SqlConnection(Connection("master"));
        await master.OpenAsync();
        foreach (var database in created.ToArray())
        {
            using var pooled = new SqlConnection(Connection(database));
            SqlConnection.ClearPool(pooled);
            using var command = new SqlCommand($"""
                IF DB_ID(N'{database}') IS NOT NULL
                BEGIN
                    ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                    DROP DATABASE [{database}];
                END
                """, master) { CommandTimeout = 120 };
            await command.ExecuteNonQueryAsync();
            created.Remove(database);
        }
    }
}
