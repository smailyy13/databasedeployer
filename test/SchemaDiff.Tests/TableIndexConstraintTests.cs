using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Değişen tablolarda index ve constraint üretimi. Snapshot yapısal index/constraint
/// verisini yalnızca KeepDisplayScripts açıkken taşır.
/// </summary>
public class TableIndexConstraintTests
{
    private const int CustomerId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        CustomerId, id, name, "sys", "int", 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null);

    private static IndexRow Idx(int indexId, string name, bool pk = false, bool uq = false, string? filter = null) =>
        new(CustomerId, indexId, name, pk ? "CLUSTERED" : "NONCLUSTERED", uq, pk, uq, 0, false, false, filter);

    private static IndexRow IdxC(int indexId, string name, string? compression, bool pk = false) =>
        new(CustomerId, indexId, name, pk ? "CLUSTERED" : "NONCLUSTERED", pk, pk, false, 0, false, false, null, compression);

    private static IndexColumnRow Key(int indexId, int columnId, byte ordinal, bool included = false) =>
        new(CustomerId, indexId, columnId, columnId, included ? (byte)0 : ordinal, false, included);

    private static CatalogSet Table(List<IndexRow> indexes, List<IndexColumnRow> idxCols,
        List<CheckConstraintRow>? checks = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id"), Col(2, "Name"), Col(3, "Email")],
        Indexes = indexes,
        IndexColumns = idxCols,
        CheckConstraints = checks ?? [],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static TableScriptResult Generate(DatabaseSnapshot source, DatabaseSnapshot target) =>
        TableScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, TableScriptOptions.Default);

    [Fact]
    public void Changed_index_include_is_dropped_and_recreated()
    {
        // Customer senaryosu: aynı index, kaynakta INCLUDE (Name) var, hedefte yok.
        var source = Build(Table(
            [Idx(2, "IX_Customer_Email")],
            [Key(2, 3, 1), Key(2, 2, 0, included: true)]));
        var target = Build(Table(
            [Idx(2, "IX_Customer_Email")],
            [Key(2, 3, 1)]));

        var script = Generate(source, target);

        Assert.Contains("DROP INDEX [IX_Customer_Email] ON [dbo].[Customer];", script.Sql);
        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_Customer_Email] ON [dbo].[Customer] ([Email] ASC) INCLUDE ([Name]);", script.Sql);
    }

    [Fact]
    public void Added_index_is_created_without_drop()
    {
        var source = Build(Table([Idx(2, "IX_New")], [Key(2, 3, 1)]));
        var target = Build(Table([], []));

        var script = Generate(source, target);

        Assert.Contains("CREATE NONCLUSTERED INDEX [IX_New] ON [dbo].[Customer] ([Email] ASC);", script.Sql);
        Assert.DoesNotContain("DROP INDEX", script.Sql);
    }

    [Fact]
    public void Removed_index_is_dropped_without_create()
    {
        var source = Build(Table([], []));
        var target = Build(Table([Idx(2, "IX_Old")], [Key(2, 3, 1)]));

        var script = Generate(source, target);

        Assert.Contains("DROP INDEX [IX_Old] ON [dbo].[Customer];", script.Sql);
        Assert.DoesNotContain("CREATE NONCLUSTERED INDEX", script.Sql);
    }

    [Fact]
    public void Primary_key_change_uses_alter_table_constraint()
    {
        // PK'yı taşıyan index; adı PK_Customer. Kaynakta key Id, hedefte key Name → değişik.
        var source = Build(Table([Idx(1, "PK_Customer", pk: true)], [Key(1, 1, 1)]));
        var target = Build(Table([Idx(1, "PK_Customer", pk: true)], [Key(1, 2, 1)]));

        var script = Generate(source, target);

        Assert.Contains("ALTER TABLE [dbo].[Customer] DROP CONSTRAINT [PK_Customer];", script.Sql);
        Assert.Contains("ALTER TABLE [dbo].[Customer] ADD CONSTRAINT [PK_Customer] PRIMARY KEY CLUSTERED ([Id] ASC);", script.Sql);
    }

    [Fact]
    public void Added_check_constraint_is_emitted()
    {
        var source = Build(Table([], [],
            [new CheckConstraintRow(CustomerId, "CK_Positive", false, "([Id]>(0))", false, false)]));
        var target = Build(Table([], []));

        var script = Generate(source, target);

        Assert.Contains("ALTER TABLE [dbo].[Customer] WITH CHECK ADD CONSTRAINT [CK_Positive] CHECK ([Id]>(0));", script.Sql);
    }

    [Fact]
    public void Changed_check_constraint_is_dropped_and_readded()
    {
        var source = Build(Table([], [],
            [new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(10))", false, false)]));
        var target = Build(Table([], [],
            [new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(0))", false, false)]));

        var script = Generate(source, target);

        Assert.Contains("DROP CONSTRAINT [CK_X];", script.Sql);
        Assert.Contains("ADD CONSTRAINT [CK_X] CHECK ([Id]>(10));", script.Sql);
    }

    [Fact]
    public void Default_added_to_existing_column_emits_add_constraint()
    {
        var key = TestFactory.Table("T");
        var source = TestFactory.Database("dev", [TestFactory.Obj(key, 1,
            columns: [TestFactory.Column("Amount", defaultDefinition: "((0))", defaultName: "DF_T_Amount")])]);
        var target = TestFactory.Database("prod", [TestFactory.Obj(key, 2,
            columns: [TestFactory.Column("Amount")], rowCount: 10)]);

        var script = TableScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, TableScriptOptions.Default);

        Assert.Contains("ALTER TABLE [dbo].[T] ADD CONSTRAINT [DF_T_Amount] DEFAULT ((0)) FOR [Amount];", script.Sql);
    }

    [Fact]
    public void Default_removed_from_existing_column_emits_drop_constraint()
    {
        var key = TestFactory.Table("T");
        var source = TestFactory.Database("dev", [TestFactory.Obj(key, 1,
            columns: [TestFactory.Column("Amount")])]);
        var target = TestFactory.Database("prod", [TestFactory.Obj(key, 2,
            columns: [TestFactory.Column("Amount", defaultDefinition: "((0))", defaultName: "DF_T_Amount")], rowCount: 10)]);

        var script = TableScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, TableScriptOptions.Default);

        Assert.Contains("ALTER TABLE [dbo].[T] DROP CONSTRAINT [DF_T_Amount];", script.Sql);
    }

    [Fact]
    public void Changed_default_drops_old_and_adds_new()
    {
        var key = TestFactory.Table("T");
        var source = TestFactory.Database("dev", [TestFactory.Obj(key, 1,
            columns: [TestFactory.Column("Amount", defaultDefinition: "((100))", defaultName: "DF_new")])]);
        var target = TestFactory.Database("prod", [TestFactory.Obj(key, 2,
            columns: [TestFactory.Column("Amount", defaultDefinition: "((0))", defaultName: "DF_old")], rowCount: 10)]);

        var script = TableScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, TableScriptOptions.Default);

        Assert.Contains("DROP CONSTRAINT [DF_old];", script.Sql);
        Assert.Contains("ADD CONSTRAINT [DF_new] DEFAULT ((100)) FOR [Amount];", script.Sql);
    }

    [Fact]
    public void System_named_default_added_without_name()
    {
        var key = TestFactory.Table("T");
        var source = TestFactory.Database("dev", [TestFactory.Obj(key, 1,
            columns: [TestFactory.Column("Flag", defaultDefinition: "((1))", defaultName: "DF__T__Flag__ABC", defaultIsSystemNamed: true)])]);
        var target = TestFactory.Database("prod", [TestFactory.Obj(key, 2,
            columns: [TestFactory.Column("Flag")], rowCount: 10)]);

        var script = TableScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, TableScriptOptions.Default);

        Assert.Contains("ALTER TABLE [dbo].[T] ADD DEFAULT ((1)) FOR [Flag];", script.Sql);
        Assert.DoesNotContain("DF__T__Flag__ABC", script.Sql);
    }

    [Fact]
    public void Foreign_key_added_and_removed_ordering()
    {
        // Order tablosu Customer'a FK ile bağlanır; kaynakta var, hedefte yok.
        const int orderId = 200;
        CatalogSet WithFk(bool hasFk) => new()
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Objects =
            [
                new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
                new ObjectRow(orderId, "dbo", "Order", "U", DateTime.UnixEpoch, 0),
            ],
            Columns =
            [
                Col(1, "Id"),
                new ColumnRow(orderId, 1, "CustomerId", "sys", "int", 4, 10, 0, false, null, false, false, null, null, null, null, null, null, null),
            ],
            ForeignKeys = hasFk
                ? [new ForeignKeyRow(900, orderId, "FK_Order_Customer", false, CustomerId, 0, 0, false, false)]
                : [],
            ForeignKeyColumns = hasFk
                ? [new ForeignKeyColumnRow(900, 1, orderId, 1, CustomerId, 1)]
                : [],
        };

        var script = TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(WithFk(true)), Build(WithFk(false))), null, TableScriptOptions.Default);

        Assert.Contains("ADD CONSTRAINT [FK_Order_Customer] FOREIGN KEY ([CustomerId]) REFERENCES [dbo].[Customer] ([Id]);", script.Sql);
    }

    [Fact]
    public void Foreign_key_add_is_emitted_in_global_section_after_table_changes()
    {
        // Bir tabloya kolon eklenir + FK eklenir → FK add, kolon değişikliğinden SONRA
        // (global FK bölümü en sonda) gelmeli.
        const int orderId = 200;
        CatalogSet Cat(bool withFk) => new()
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Objects =
            [
                new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
                new ObjectRow(orderId, "dbo", "Orders", "U", DateTime.UnixEpoch, 0),
            ],
            Columns = withFk
                ? [Col(1, "Id"),
                   new ColumnRow(orderId, 1, "CustomerId", "sys", "int", 4, 10, 0, false, null, false, false, null, null, null, null, null, null, null),
                   new ColumnRow(orderId, 2, "Note", "sys", "int", 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null)]
                : [Col(1, "Id"),
                   new ColumnRow(orderId, 1, "CustomerId", "sys", "int", 4, 10, 0, false, null, false, false, null, null, null, null, null, null, null)],
            ForeignKeys = withFk ? [new ForeignKeyRow(900, orderId, "FK_Orders_Customer", false, CustomerId, 0, 0, false, false)] : [],
            ForeignKeyColumns = withFk ? [new ForeignKeyColumnRow(900, 1, orderId, 1, CustomerId, 1)] : [],
        };

        var script = TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(Cat(true)), Build(Cat(false))), null, TableScriptOptions.Default);

        var colIdx = script.Sql.IndexOf("ADD [Note]", StringComparison.Ordinal);
        var fkIdx = script.Sql.IndexOf("ADD CONSTRAINT [FK_Orders_Customer]", StringComparison.Ordinal);
        Assert.True(colIdx >= 0 && fkIdx >= 0 && colIdx < fkIdx, "FK add kolon değişikliğinden sonra gelmeli");
        Assert.Contains("Foreign key''ler ekleniyor", script.Sql);
    }

    // --- DATA_COMPRESSION (Dalga 2) ---

    [Fact]
    public void Added_index_with_page_compression_emits_with_clause()
    {
        var source = Build(Table([IdxC(2, "IX_C", "PAGE")], [Key(2, 3, 1)]));
        var script = Generate(source, Build(Table([], [])));

        Assert.Contains(
            "CREATE NONCLUSTERED INDEX [IX_C] ON [dbo].[Customer] ([Email] ASC) WITH (DATA_COMPRESSION = PAGE);",
            script.Sql);
    }

    [Fact]
    public void Compression_change_none_to_page_recreates_index_with_clause()
    {
        var source = Build(Table([IdxC(2, "IX_C", "PAGE")], [Key(2, 3, 1)]));
        var target = Build(Table([IdxC(2, "IX_C", "NONE")], [Key(2, 3, 1)]));

        var script = Generate(source, target);

        Assert.Contains("DROP INDEX [IX_C] ON [dbo].[Customer];", script.Sql);
        Assert.Contains("WITH (DATA_COMPRESSION = PAGE)", script.Sql);
    }

    [Fact]
    public void Primary_key_with_page_compression_emits_with_clause()
    {
        var source = Build(Table([IdxC(1, "PK_Customer", "PAGE", pk: true)], [Key(1, 1, 1)]));
        var script = Generate(source, Build(Table([], [])));

        Assert.Contains(
            "ADD CONSTRAINT [PK_Customer] PRIMARY KEY CLUSTERED ([Id] ASC) WITH (DATA_COMPRESSION = PAGE);",
            script.Sql);
    }

    [Fact]
    public void Plain_columnstore_is_not_emitted_as_explicit_compression()
    {
        // Düz COLUMNSTORE tipin doğasında; WITH (DATA_COMPRESSION = COLUMNSTORE) yazılmamalı.
        var source = Build(Table([IdxC(2, "IX_C", "COLUMNSTORE")], [Key(2, 3, 1)]));
        var script = Generate(source, Build(Table([], [])));

        Assert.DoesNotContain("DATA_COMPRESSION = COLUMNSTORE", script.Sql);
    }

    [Fact]
    public void Ignore_data_compression_option_suppresses_diff()
    {
        var opts = SnapshotOptions.Default with { KeepDisplayScripts = true, IgnoreDataCompression = true };
        var source = SnapshotBuilder.Build(Table([IdxC(2, "IX_C", "PAGE")], [Key(2, 3, 1)]), new ExtractionReport(), opts);
        var target = SnapshotBuilder.Build(Table([IdxC(2, "IX_C", "NONE")], [Key(2, 3, 1)]), new ExtractionReport(), opts);

        Assert.Empty(SchemaComparer.Compare(source, target).Differences);
    }
}
