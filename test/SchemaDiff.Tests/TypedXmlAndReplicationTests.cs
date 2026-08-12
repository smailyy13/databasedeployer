using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Tipli XML kolonları · NOT FOR REPLICATION · index kilit seçenekleri.
///
/// Üçü de "eksik obje" değil **yanlış DDL** sınıfından: katalogda id/bayrak olarak durup
/// script'e yansımadıklarında hedefte obje oluşur ama başka bir obje olur.
/// </summary>
public class TypedXmlAndReplicationTests
{
    private const int CustomerId = 100;
    private const int CollectionId = 65536;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        CustomerId, id, name, "sys", "int", 4, 10, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    private static ColumnRow XmlCol(int id, string name, int collectionId = CollectionId, bool document = false) =>
        new(CustomerId, id, name, "sys", "xml", -1, 0, 0, true, null, false, false,
            null, null, null, null, null, null, null, 0, false, false, false, false, false,
            collectionId, document);

    private static ColumnRow IdentityCol(int id, string name, bool notForReplication) =>
        new(CustomerId, id, name, "sys", "int", 4, 10, 0, false, null, true, false,
            null, null, null, null, null, "1", "1", 0, false, false, false, false, false,
            0, false, notForReplication);

    private static IndexRow Idx(int indexId, string name, bool rowLocks = true, bool pageLocks = true) =>
        new(CustomerId, indexId, name, "NONCLUSTERED", false, false, false, 0, false, false, null, null,
            rowLocks, pageLocks);

    private static IndexColumnRow Key(int indexId, int columnId) =>
        new(CustomerId, indexId, columnId, columnId, 1, false, false);

    private static CatalogSet Table(
        List<ColumnRow>? columns = null,
        List<CheckConstraintRow>? checks = null,
        List<ForeignKeyRow>? foreignKeys = null,
        List<IndexRow>? indexes = null,
        List<IndexColumnRow>? indexColumns = null,
        bool withCollection = true) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = columns ?? [Col(1, "Id")],
        CheckConstraints = checks ?? [],
        ForeignKeys = foreignKeys ?? [],
        ForeignKeyColumns = foreignKeys is { Count: > 0 } ? [new ForeignKeyColumnRow(900, 1, CustomerId, 1, CustomerId, 1)] : [],
        Indexes = indexes ?? [],
        IndexColumns = indexColumns ?? [],
        XmlSchemaCollections = withCollection ? [new XmlSchemaCollectionRow(CollectionId, "dbo", "OrderSchema")] : [],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(DatabaseSnapshot source, DatabaseSnapshot target) =>
        TableScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, TableScriptOptions.Default).Sql;

    private static string CreateScript(CatalogSet catalog) =>
        Build(catalog).Objects[TestFactory.Table("Customer")].DisplayScript!;

    // --- tipli XML ---

    [Fact]
    public void Typed_xml_column_writes_its_collection_in_create_table()
    {
        Assert.Contains("XML (CONTENT [dbo].[OrderSchema])", CreateScript(Table([XmlCol(1, "Payload")])));
    }

    [Fact]
    public void Typed_xml_document_variant_is_written()
    {
        Assert.Contains("XML (DOCUMENT [dbo].[OrderSchema])",
            CreateScript(Table([XmlCol(1, "Payload", document: true)])));
    }

    [Fact]
    public void Untyped_xml_column_stays_plain()
    {
        Assert.Contains("XML", CreateScript(Table([XmlCol(1, "Payload", collectionId: 0)])));
        Assert.DoesNotContain("CONTENT", CreateScript(Table([XmlCol(1, "Payload", collectionId: 0)])));
    }

    [Fact]
    public void Typed_and_untyped_xml_are_not_equal()
    {
        // Asıl boşluk buydu: iki taraf da "xml" göründüğü için fark hiç fark edilmiyordu.
        var typed = Build(Table([XmlCol(1, "Payload")]));
        var untyped = Build(Table([XmlCol(1, "Payload", collectionId: 0)]));

        Assert.Single(SchemaComparer.Compare(typed, untyped).Differences);
    }

    [Fact]
    public void Content_and_document_differ()
    {
        var content = Build(Table([XmlCol(1, "Payload")]));
        var document = Build(Table([XmlCol(1, "Payload", document: true)]));

        Assert.Single(SchemaComparer.Compare(content, document).Differences);
    }

    [Fact]
    public void Collection_is_compared_by_name_not_id()
    {
        // Koleksiyon id'si ortamlar arasında farklıdır; ad aynıysa fark OLMAMALI.
        var left = Build(Table([XmlCol(1, "Payload", collectionId: 65536)]));
        var right = Build(new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
            Columns = [new ColumnRow(CustomerId, 1, "Payload", "sys", "xml", -1, 0, 0, true, null, false, false,
                null, null, null, null, null, null, null, 0, false, false, false, false, false, 99999, false)],
            XmlSchemaCollections = [new XmlSchemaCollectionRow(99999, "dbo", "OrderSchema")],
        });

        Assert.Empty(SchemaComparer.Compare(left, right).Differences);
    }

    [Fact]
    public void Added_typed_xml_column_keeps_its_collection()
    {
        var script = Script(Build(Table([Col(1, "Id"), XmlCol(2, "Payload")])), Build(Table([Col(1, "Id")])));

        Assert.Contains("ADD [Payload] xml(CONTENT [dbo].[OrderSchema]) NULL;", script);
    }

    [Fact]
    public void Unresolvable_collection_is_not_written_as_a_guess()
    {
        // Koleksiyon sorgusu düşmüşse ad bilinmiyordur; uydurmaktansa düz xml yazılır.
        Assert.DoesNotContain("CONTENT", CreateScript(Table([XmlCol(1, "Payload")], withCollection: false)));
    }

    // --- NOT FOR REPLICATION ---

    [Fact]
    public void Identity_not_for_replication_is_written_in_create_table()
    {
        Assert.Contains("IDENTITY (1, 1) NOT FOR REPLICATION",
            CreateScript(Table([IdentityCol(1, "Id", notForReplication: true)])));
    }

    [Fact]
    public void Identity_not_for_replication_difference_is_detected()
    {
        var with = Build(Table([IdentityCol(1, "Id", true)]));
        var without = Build(Table([IdentityCol(1, "Id", false)]));

        Assert.Single(SchemaComparer.Compare(with, without).Differences);
    }

    [Fact]
    public void Identity_not_for_replication_change_is_skipped_with_a_reason()
    {
        var result = TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(Table([IdentityCol(1, "Id", true)])), Build(Table([IdentityCol(1, "Id", false)]))),
            null, TableScriptOptions.Default);

        Assert.Contains(result.Skipped, s => s.Reason.Contains("NOT FOR REPLICATION", StringComparison.Ordinal));
    }

    [Fact]
    public void Added_identity_column_keeps_identity_and_replication_flag()
    {
        // ADD COLUMN yolunda IDENTITY hiç yazılmıyordu: kolon sıradan kolon olarak eklenirdi.
        var script = Script(
            Build(Table([Col(1, "Other"), IdentityCol(2, "Id", notForReplication: true)])),
            Build(Table([Col(1, "Other")])));

        Assert.Contains("ADD [Id] int IDENTITY(1, 1) NOT FOR REPLICATION NOT NULL;", script);
    }

    [Fact]
    public void Check_constraint_not_for_replication_is_written()
    {
        var check = new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(0))", false, false, true);
        var script = Script(Build(Table(checks: [check])), Build(Table()));

        Assert.Contains("ADD CONSTRAINT [CK_X] CHECK NOT FOR REPLICATION ([Id]>(0));", script);
    }

    [Fact]
    public void Check_constraint_replication_flag_change_recreates_it()
    {
        var with = new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(0))", false, false, true);
        var without = new CheckConstraintRow(CustomerId, "CK_X", false, "([Id]>(0))", false, false, false);

        var script = Script(Build(Table(checks: [with])), Build(Table(checks: [without])));

        Assert.Contains("DROP CONSTRAINT [CK_X];", script);
        Assert.Contains("CHECK NOT FOR REPLICATION", script);
    }

    [Fact]
    public void Foreign_key_not_for_replication_is_written()
    {
        var fk = new ForeignKeyRow(900, CustomerId, "FK_Self", false, CustomerId, 0, 0, false, false, true);
        var script = Script(Build(Table(foreignKeys: [fk])), Build(Table()));

        Assert.Contains("NOT FOR REPLICATION;", script);
    }

    // --- index kilit seçenekleri ---

    [Fact]
    public void Index_lock_options_are_written_only_when_off()
    {
        var defaults = CreateScript(Table([Col(1, "Id")], indexes: [Idx(2, "IX_A")], indexColumns: [Key(2, 1)]));
        Assert.DoesNotContain("ALLOW_ROW_LOCKS", defaults);

        var off = CreateScript(Table([Col(1, "Id")],
            indexes: [Idx(2, "IX_A", rowLocks: false, pageLocks: false)], indexColumns: [Key(2, 1)]));
        Assert.Contains("WITH (ALLOW_ROW_LOCKS = OFF, ALLOW_PAGE_LOCKS = OFF)", off);
    }

    [Fact]
    public void Lock_option_difference_recreates_the_index()
    {
        var source = Build(Table([Col(1, "Id")], indexes: [Idx(2, "IX_A", pageLocks: false)], indexColumns: [Key(2, 1)]));
        var target = Build(Table([Col(1, "Id")], indexes: [Idx(2, "IX_A")], indexColumns: [Key(2, 1)]));

        var script = Script(source, target);

        Assert.Contains("DROP INDEX [IX_A] ON [dbo].[Customer];", script);
        Assert.Contains("WITH (ALLOW_PAGE_LOCKS = OFF);", script);
    }

    [Fact]
    public void Ignore_index_physical_options_suppresses_lock_differences()
    {
        var opts = SnapshotOptions.Default with { KeepDisplayScripts = true, IgnoreIndexPhysicalOptions = true };
        var source = SnapshotBuilder.Build(
            Table([Col(1, "Id")], indexes: [Idx(2, "IX_A", rowLocks: false)], indexColumns: [Key(2, 1)]),
            new ExtractionReport(), opts);
        var target = SnapshotBuilder.Build(
            Table([Col(1, "Id")], indexes: [Idx(2, "IX_A")], indexColumns: [Key(2, 1)]),
            new ExtractionReport(), opts);

        Assert.Empty(SchemaComparer.Compare(source, target).Differences);
    }

    [Fact]
    public void Compression_and_lock_options_share_one_with_clause()
    {
        // İki ayrı WITH(...) yazmak geçersiz T-SQL üretir.
        var index = new IndexRow(CustomerId, 2, "IX_A", "NONCLUSTERED", false, false, false,
            0, false, false, null, "PAGE", true, false);
        var script = Script(
            Build(Table([Col(1, "Id")], indexes: [index], indexColumns: [Key(2, 1)])),
            Build(Table([Col(1, "Id")])));

        Assert.Contains("WITH (DATA_COMPRESSION = PAGE, ALLOW_PAGE_LOCKS = OFF);", script);
    }
}
