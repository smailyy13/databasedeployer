using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

public class TypeScriptGeneratorTests
{
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static CatalogSet AliasCatalog(string name, string baseType, short maxLen, byte prec, byte scale, bool nullable) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        UserDefinedTypes = [new UserDefinedTypeRow(1, "dbo", name, baseType, maxLen, prec, scale, nullable, null)],
    };

    private static CatalogSet TableTypeCatalog(string name, params (int id, string col, string type, short len, bool nullable)[] cols) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        TableTypes = [new TableTypeRow(1, "dbo", name, 500)],
        TableTypeColumns = [.. cols.Select(c =>
            new TableTypeColumnRow(500, c.id, c.col, "sys", c.type, c.len, 10, 0, c.nullable, null, false, false))],
    };

    private static CatalogSet Empty() => new() { DatabaseName = "test", ServerName = "TESTSRV" };

    private static TypeScriptResult Generate(DatabaseSnapshot source, DatabaseSnapshot target, TypeScriptOptions? o = null) =>
        TypeScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, o ?? TypeScriptOptions.Default);

    [Fact]
    public void Added_alias_type_emits_create_guarded()
    {
        var script = Generate(Build(AliasCatalog("Money", "decimal", 9, 19, 4, false)), Build(Empty()));

        Assert.Contains("CREATE TYPE [dbo].[Money] FROM [decimal](19,4) NOT NULL;", script.Sql);
        Assert.Contains("TYPE_ID(N'[dbo].[Money]') IS NULL", script.Sql);
    }

    [Fact]
    public void Added_varchar_alias_renders_length()
    {
        var script = Generate(Build(AliasCatalog("Code", "varchar", 10, 0, 0, true)), Build(Empty()));
        Assert.Contains("FROM [varchar](10) NULL;", script.Sql);
    }

    [Fact]
    public void Added_table_type_emits_create_with_columns()
    {
        var source = Build(TableTypeCatalog("IdList", (1, "Id", "int", 4, false), (2, "Note", "varchar", 50, true)));
        var script = Generate(source, Build(Empty()));

        Assert.Contains("CREATE TYPE [dbo].[IdList] AS TABLE (", script.Sql);
        Assert.Contains("[Id] [int] NOT NULL", script.Sql);
        Assert.Contains("[Note] [varchar](50) NULL", script.Sql);
    }

    [Fact]
    public void Changed_type_is_skipped_not_altered()
    {
        var source = Build(AliasCatalog("Money", "decimal", 9, 19, 4, false));
        var target = Build(AliasCatalog("Money", "decimal", 5, 10, 2, false));

        var script = Generate(source, target);

        Assert.DoesNotContain("CREATE TYPE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key.Name == "Money" && s.Reason.Contains("drop+recreate"));
    }

    [Fact]
    public void Removed_type_not_dropped_by_default()
    {
        var script = Generate(Build(Empty()), Build(AliasCatalog("Legacy", "int", 4, 10, 0, true)));

        Assert.DoesNotContain("DROP TYPE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key.Name == "Legacy");
    }

    [Fact]
    public void Removed_type_dropped_when_enabled()
    {
        var result = SchemaComparer.Compare(Build(Empty()), Build(AliasCatalog("Legacy", "int", 4, 10, 0, true)));
        var script = TypeScriptGenerator.Generate(result, null, new TypeScriptOptions { IncludeDrops = true });

        Assert.Contains("DROP TYPE [dbo].[Legacy];", script.Sql);
        Assert.Contains("TYPE_ID(N'[dbo].[Legacy]') IS NOT NULL", script.Sql);
    }

    private static CatalogSet SequenceCatalog(string name, string type = "bigint") => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(300, "dbo", name, "SO", DateTime.UnixEpoch, 0)],
        Sequences = [new SequenceRow(300, type, 19, 0, "1", "1", "-9223372036854775808", "9223372036854775807", false, true, 50)],
    };

    private static CatalogSet SynonymCatalog(string name, string baseObject) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(301, "dbo", name, "SN", DateTime.UnixEpoch, 0)],
        Synonyms = [new SynonymRow(301, baseObject)],
    };

    [Fact]
    public void Added_sequence_emits_create_guarded()
    {
        var script = Generate(Build(SequenceCatalog("OrderId")), Build(Empty()));

        Assert.Contains("CREATE SEQUENCE [dbo].[OrderId]", script.Sql);
        Assert.Contains("AS [bigint]", script.Sql);
        Assert.Contains("OBJECT_ID(N'[dbo].[OrderId]', N'SO') IS NULL", script.Sql);
    }

    [Fact]
    public void Removed_sequence_dropped_when_enabled()
    {
        var result = SchemaComparer.Compare(Build(Empty()), Build(SequenceCatalog("Old")));
        var script = TypeScriptGenerator.Generate(result, null, new TypeScriptOptions { IncludeDrops = true });

        Assert.Contains("DROP SEQUENCE [dbo].[Old];", script.Sql);
    }

    [Fact]
    public void Added_synonym_emits_create()
    {
        var script = Generate(Build(SynonymCatalog("CustAlias", "[dbo].[Customers]")), Build(Empty()));

        Assert.Contains("CREATE SYNONYM [dbo].[CustAlias] FOR [dbo].[Customers];", script.Sql);
        Assert.Contains("OBJECT_ID(N'[dbo].[CustAlias]', N'SN') IS NULL", script.Sql);
    }

    [Fact]
    public void Sequences_created_before_alias_types()
    {
        var source = Build(new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Objects = [new ObjectRow(300, "dbo", "Seq", "SO", DateTime.UnixEpoch, 0)],
            Sequences = [new SequenceRow(300, "int", 10, 0, "1", "1", "0", "100", false, false, null)],
            UserDefinedTypes = [new UserDefinedTypeRow(1, "dbo", "Money", "decimal", 9, 19, 4, false, null)],
        });
        var script = Generate(source, Build(Empty()));

        var seqIdx = script.Sql.IndexOf("[dbo].[Seq]", StringComparison.Ordinal);
        var typeIdx = script.Sql.IndexOf("[dbo].[Money]", StringComparison.Ordinal);
        Assert.True(seqIdx >= 0 && typeIdx >= 0 && seqIdx < typeIdx);
    }

    private static CatalogSet PartitionFnCatalog(string name, string type, bool right, params string[] bounds) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        PartitionFunctions = [new PartitionFunctionRow(1, name, right, type)],
        PartitionRangeValues = [.. bounds.Select((b, i) => new PartitionRangeValueRow(1, i, b))],
    };

    [Fact]
    public void Added_partition_function_emits_create_with_numeric_literals()
    {
        var script = Generate(Build(PartitionFnCatalog("pfMonthly", "int", true, "10", "20", "30")), Build(Empty()));

        Assert.Contains("CREATE PARTITION FUNCTION [pfMonthly](int) AS RANGE RIGHT FOR VALUES (10, 20, 30);", script.Sql);
        Assert.Contains("NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pfMonthly')", script.Sql);
    }

    [Fact]
    public void Partition_function_date_boundaries_are_quoted()
    {
        var script = Generate(Build(PartitionFnCatalog("pfDate", "date", false, "2025-01-01", "2025-02-01")), Build(Empty()));

        Assert.Contains("FOR VALUES (N'2025-01-01', N'2025-02-01')", script.Sql);
    }

    [Fact]
    public void Added_partition_scheme_emits_create()
    {
        var source = Build(new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            PartitionSchemes = [new PartitionSchemeRow(70, "psMonthly", "pfMonthly")],
            PartitionSchemeFiles = [new PartitionSchemeFileRow(70, 0, "PRIMARY"), new PartitionSchemeFileRow(70, 1, "FG1")],
        });
        var script = Generate(source, Build(Empty()));

        Assert.Contains("CREATE PARTITION SCHEME [psMonthly] AS PARTITION [pfMonthly] TO ([PRIMARY], [FG1]);", script.Sql);
    }

    [Fact]
    public void Partition_function_created_before_scheme()
    {
        var source = Build(new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            PartitionFunctions = [new PartitionFunctionRow(1, "pf", true, "int")],
            PartitionRangeValues = [new PartitionRangeValueRow(1, 0, "10")],
            PartitionSchemes = [new PartitionSchemeRow(70, "ps", "pf")],
            PartitionSchemeFiles = [new PartitionSchemeFileRow(70, 0, "PRIMARY")],
        });
        var script = Generate(source, Build(Empty()));

        var fnIdx = script.Sql.IndexOf("PARTITION FUNCTION [pf]", StringComparison.Ordinal);
        var scIdx = script.Sql.IndexOf("PARTITION SCHEME [ps]", StringComparison.Ordinal);
        Assert.True(fnIdx >= 0 && scIdx >= 0 && fnIdx < scIdx);
    }

    [Fact]
    public void Alias_types_are_created_before_table_types()
    {
        var source = Build(new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            UserDefinedTypes = [new UserDefinedTypeRow(1, "dbo", "Money", "decimal", 9, 19, 4, false, null)],
            TableTypes = [new TableTypeRow(2, "dbo", "Rows", 500)],
            TableTypeColumns = [new TableTypeColumnRow(500, 1, "Id", "sys", "int", 4, 10, 0, false, null, false, false)],
        });
        var script = Generate(source, Build(Empty()));

        var aliasIdx = script.Sql.IndexOf("[dbo].[Money]", StringComparison.Ordinal);
        var tableIdx = script.Sql.IndexOf("[dbo].[Rows]", StringComparison.Ordinal);
        Assert.True(aliasIdx >= 0 && tableIdx >= 0 && aliasIdx < tableIdx);
    }
}
