using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

/// <summary>
/// Table type'ların PK / UNIQUE / CHECK / index'leri ve kolon DEFAULT'ları.
///
/// Table type ALTER edilemez: bir farkın script'i yoktur, drop+recreate gerekir. Bu yüzden
/// buradaki değer GÖRÜNÜRLÜKTÜR — önceden yalnız kolon yapısı kıyaslandığı için, PK'sı ya da
/// CHECK'i farklı iki tip "aynı" görünüyordu.
/// </summary>
public class TableTypeConstraintTests
{
    private const int TypeTableId = 500;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static TableTypeColumnRow Col(
        int id, string name, bool nullable = true, string? defaultDefinition = null) =>
        new(TypeTableId, id, name, "sys", "int", 4, 10, 0, nullable, null, false, false, defaultDefinition);

    private static TableTypeIndexRow Index(
        int indexId, string? name, bool pk = false, bool uq = false, bool unique = false,
        string? constraintName = null, bool constraintSystemNamed = true) =>
        new(TypeTableId, indexId, name, pk ? "CLUSTERED" : "NONCLUSTERED",
            unique || pk || uq, pk, uq, constraintName, constraintSystemNamed);

    private static IndexColumnRow Key(int indexId, int columnId, byte ordinal = 1, bool descending = false) =>
        new(TypeTableId, indexId, columnId, columnId, ordinal, descending, false);

    private static CatalogSet Type(
        List<TableTypeColumnRow>? columns = null,
        List<TableTypeIndexRow>? indexes = null,
        List<IndexColumnRow>? indexColumns = null,
        List<TableTypeCheckRow>? checks = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        TableTypes = [new TableTypeRow(300, "dbo", "IdList", TypeTableId)],
        TableTypeColumns = columns ?? [Col(1, "Id", nullable: false), Col(2, "Name")],
        TableTypeIndexes = indexes ?? [],
        TableTypeIndexColumns = indexColumns ?? [],
        TableTypeChecks = checks ?? [],
    };

    private static readonly ObjectKey TypeKey = new("dbo", "IdList", ObjectKind.TableType);

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(CatalogSet c) => Build(c).Objects[TypeKey].DisplayScript!;

    private static CatalogSet WithPrimaryKey() =>
        Type(indexes: [Index(1, "PK__IdList__ABC", pk: true)], indexColumns: [Key(1, 1)]);

    // --- karşılaştırma ---

    [Fact]
    public void Primary_key_difference_is_detected()
    {
        // Asıl boşluk buydu: kolonları aynı, PK'sı farklı iki tip "aynı" görünüyordu.
        var diff = Assert.Single(SchemaComparer.Compare(Build(WithPrimaryKey()), Build(Type())).Differences);

        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("constraints", diff.ChangedParts);
    }

    [Fact]
    public void Identical_constraints_produce_no_difference()
    {
        Assert.Empty(SchemaComparer.Compare(Build(WithPrimaryKey()), Build(WithPrimaryKey())).Differences);
    }

    [Fact]
    public void Check_constraint_difference_is_detected()
    {
        var withCheck = Type(checks: [new TableTypeCheckRow(TypeTableId, "CK__IdList__X", true, "([Id]>(0))")]);

        Assert.Single(SchemaComparer.Compare(Build(withCheck), Build(Type())).Differences);
    }

    [Fact]
    public void Index_difference_is_detected()
    {
        var withIndex = Type(indexes: [Index(2, "IX_Name")], indexColumns: [Key(2, 2)]);

        Assert.Single(SchemaComparer.Compare(Build(withIndex), Build(Type())).Differences);
    }

    [Fact]
    public void Key_column_difference_is_detected()
    {
        var onId = Type(indexes: [Index(1, "PK_A", pk: true)], indexColumns: [Key(1, 1)]);
        var onName = Type(indexes: [Index(1, "PK_A", pk: true)], indexColumns: [Key(1, 2)]);

        Assert.Single(SchemaComparer.Compare(Build(onId), Build(onName)).Differences);
    }

    [Fact]
    public void Column_default_difference_is_detected()
    {
        var withDefault = Type([Col(1, "Id", nullable: false), Col(2, "Name", defaultDefinition: "((0))")]);

        Assert.Single(SchemaComparer.Compare(Build(withDefault), Build(Type())).Differences);
    }

    [Fact]
    public void Constraint_order_in_catalog_does_not_affect_the_hash()
    {
        var a = Type(
            indexes: [Index(1, "PK_A", pk: true), Index(2, "IX_Name")],
            indexColumns: [Key(1, 1), Key(2, 2)]);
        var b = Type(
            indexes: [Index(2, "IX_Name"), Index(1, "PK_A", pk: true)],
            indexColumns: [Key(2, 2), Key(1, 1)]);

        Assert.Empty(SchemaComparer.Compare(Build(a), Build(b)).Differences);
    }

    // --- CREATE TYPE metni ---

    [Fact]
    public void Primary_key_is_written_without_its_system_name()
    {
        // Sistem üretimi ad ortamlar arasında farklıdır; yazılırsa gürültü olur.
        var script = Script(WithPrimaryKey());

        Assert.Contains("PRIMARY KEY CLUSTERED ([Id] ASC)", script);
        Assert.DoesNotContain("PK__IdList__ABC", script);
    }

    [Fact]
    public void User_named_constraint_keeps_its_name()
    {
        var named = Type(
            indexes: [Index(1, "PK_IdList", pk: true, constraintName: "PK_IdList", constraintSystemNamed: false)],
            indexColumns: [Key(1, 1)]);

        Assert.Contains("CONSTRAINT [PK_IdList] PRIMARY KEY CLUSTERED ([Id] ASC)", Script(named));
    }

    [Fact]
    public void Unique_constraint_is_written()
    {
        var unique = Type(indexes: [Index(2, "UQ__IdList__X", uq: true)], indexColumns: [Key(2, 2)]);

        Assert.Contains("UNIQUE NONCLUSTERED ([Name] ASC)", Script(unique));
    }

    [Fact]
    public void Standalone_index_is_written_inline()
    {
        // Table type'ta bağımsız index satır içi yazılır (SQL 2014+).
        var withIndex = Type(indexes: [Index(2, "IX_Name")], indexColumns: [Key(2, 2)]);

        Assert.Contains("INDEX [IX_Name] NONCLUSTERED ([Name] ASC)", Script(withIndex));
    }

    [Fact]
    public void Check_constraint_is_written()
    {
        var withCheck = Type(checks: [new TableTypeCheckRow(TypeTableId, "CK__IdList__X", true, "([Id]>(0))")]);

        Assert.Contains("CHECK ([Id]>(0))", Script(withCheck));
    }

    [Fact]
    public void Column_default_is_written()
    {
        var withDefault = Type([Col(1, "Id", nullable: false), Col(2, "Name", defaultDefinition: "((0))")]);

        Assert.Contains("DEFAULT ((0))", Script(withDefault));
    }

    [Fact]
    public void Constraints_follow_columns_and_are_comma_separated()
    {
        var script = Script(WithPrimaryKey());
        var lastColumn = script.IndexOf("[Name]", StringComparison.Ordinal);
        var constraint = script.IndexOf("PRIMARY KEY", StringComparison.Ordinal);

        Assert.True(lastColumn < constraint, "constraint'ler kolonlardan sonra gelmeli");
        // Son gövde satırından sonra virgül olmamalı, aksi hâlde CREATE TYPE derlenmez.
        Assert.Contains("([Id] ASC)\n);", script.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void Type_without_constraints_still_renders_valid_body()
    {
        var script = Script(Type());

        Assert.Contains("CREATE TYPE [dbo].[IdList] AS TABLE (", script);
        Assert.DoesNotContain("PRIMARY KEY", script);
    }

    // --- arayüz ---

    [Fact]
    public void Change_catalog_reports_the_constraint_change()
    {
        var result = SchemaComparer.Compare(Build(WithPrimaryKey()), Build(Type()));
        var change = Assert.Single(ChangeCatalog.Build(result));

        Assert.Equal("Table Type", change.ObjectType);
        Assert.Contains(change.Children, c => c.ItemType == "constraints" || c.Category == "Properties");
    }
}
