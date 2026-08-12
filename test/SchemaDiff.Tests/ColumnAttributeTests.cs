using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Kolon depolama nitelikleri: SPARSE · FILESTREAM · ROWGUIDCOL · COLUMN_SET.
///
/// Bunlar ayrı bir obje sınıfı değil, tablonun fiziksel tanımının parçasıdır: kaçırılırsa
/// eksik bir obje değil, YANLIŞ bir CREATE TABLE üretilir — hedefte tablo oluşur ama
/// başka bir tablo olur. Bu yüzden hem karşılaştırmada hem script'te görünmeliler.
/// </summary>
public class ColumnAttributeTests
{
    private const int CustomerId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(
        int id, string name, string type = "int", short maxLength = 4,
        bool sparse = false, bool fileStream = false, bool rowGuidCol = false, bool columnSet = false) =>
        new(CustomerId, id, name, "sys", type, maxLength, 10, 0, true, null, false, false,
            null, null, null, null, null, null, null, 0, false,
            sparse, fileStream, rowGuidCol, columnSet);

    private static CatalogSet Table(params ColumnRow[] columns) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [.. columns],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(DatabaseSnapshot source, DatabaseSnapshot target) =>
        TableScriptGenerator.Generate(SchemaComparer.Compare(source, target), null, TableScriptOptions.Default).Sql;

    private static string CreateScript(CatalogSet catalog) =>
        Build(catalog).Objects[TestFactory.Table("Customer")].DisplayScript!;

    // --- karşılaştırma ---

    [Theory]
    [InlineData("sparse")]
    [InlineData("filestream")]
    [InlineData("rowguidcol")]
    [InlineData("columnSet")]
    public void Attribute_difference_is_detected(string attribute)
    {
        var plain = Build(Table(Col(1, "C")));
        var flagged = Build(Table(attribute switch
        {
            "sparse" => Col(1, "C", sparse: true),
            "filestream" => Col(1, "C", "varbinary", -1, fileStream: true),
            "rowguidcol" => Col(1, "C", "uniqueidentifier", 16, rowGuidCol: true),
            _ => Col(1, "C", "xml", -1, columnSet: true),
        }));

        var diff = Assert.Single(SchemaComparer.Compare(flagged, plain).Differences);
        Assert.Contains("columns", diff.ChangedParts);
    }

    [Fact]
    public void Plain_column_canonical_is_unchanged_by_the_new_flags()
    {
        // Nitelikler yalnızca VAR olduklarında yazılır: varsayılan kolonların kanonik metni
        // (dolayısıyla hash'i) eskisiyle aynı kalmalı.
        var canonical = Build(Table(Col(1, "C"))).Objects[TestFactory.Table("Customer")].PartCanonical["columns"];

        Assert.DoesNotContain("sparse", canonical, StringComparison.Ordinal);
        Assert.DoesNotContain("rowguidcol", canonical, StringComparison.Ordinal);
    }

    [Fact]
    public void Sparse_flag_appears_in_the_canonical_text()
    {
        var canonical = Build(Table(Col(1, "C", sparse: true)))
            .Objects[TestFactory.Table("Customer")].PartCanonical["columns"];

        Assert.Contains("|sparse", canonical, StringComparison.Ordinal);
    }

    // --- CREATE TABLE ---

    [Fact]
    public void Create_table_writes_sparse()
    {
        Assert.Contains("SPARSE NULL", CreateScript(Table(Col(1, "C", sparse: true))));
    }

    [Fact]
    public void Create_table_writes_rowguidcol()
    {
        Assert.Contains("ROWGUIDCOL NULL", CreateScript(Table(Col(1, "G", "uniqueidentifier", 16, rowGuidCol: true))));
    }

    [Fact]
    public void Create_table_writes_filestream()
    {
        Assert.Contains("FILESTREAM NULL", CreateScript(Table(Col(1, "D", "varbinary", -1, fileStream: true))));
    }

    [Fact]
    public void Create_table_writes_column_set()
    {
        Assert.Contains("COLUMN_SET FOR ALL_SPARSE_COLUMNS",
            CreateScript(Table(Col(1, "S", "xml", -1, columnSet: true))));
    }

    [Fact]
    public void Create_table_writes_filestream_before_sparse()
    {
        // T-SQL grameri: FILESTREAM → COLLATE → SPARSE. Sıra bozulursa CREATE derlenmez.
        var script = CreateScript(Table(Col(1, "D", "varbinary", -1, sparse: true, fileStream: true)));

        Assert.True(
            script.IndexOf("FILESTREAM", StringComparison.Ordinal) <
            script.IndexOf("SPARSE", StringComparison.Ordinal),
            "FILESTREAM, SPARSE'tan önce yazılmalı");
    }

    // --- ADD COLUMN ---

    [Fact]
    public void Added_sparse_column_keeps_the_attribute()
    {
        var script = Script(Build(Table(Col(1, "Id"), Col(2, "C", sparse: true))), Build(Table(Col(1, "Id"))));

        Assert.Contains("ADD [C] int SPARSE NULL;", script);
    }

    [Fact]
    public void Added_rowguidcol_column_keeps_the_attribute()
    {
        var script = Script(
            Build(Table(Col(1, "Id"), Col(2, "G", "uniqueidentifier", 16, rowGuidCol: true))),
            Build(Table(Col(1, "Id"))));

        Assert.Contains("ROWGUIDCOL NULL;", script);
    }

    // --- niteliğin açılıp kapatılması ---

    [Fact]
    public void Sparse_is_turned_on_with_alter_column_add_sparse()
    {
        var script = Script(Build(Table(Col(1, "C", sparse: true))), Build(Table(Col(1, "C"))));

        Assert.Contains("ALTER TABLE [dbo].[Customer] ALTER COLUMN [C] ADD SPARSE;", script);
    }

    [Fact]
    public void Sparse_is_turned_off_with_alter_column_drop_sparse()
    {
        var script = Script(Build(Table(Col(1, "C"))), Build(Table(Col(1, "C", sparse: true))));

        Assert.Contains("ALTER TABLE [dbo].[Customer] ALTER COLUMN [C] DROP SPARSE;", script);
    }

    [Fact]
    public void Rowguidcol_is_toggled_with_add_and_drop()
    {
        var guid = Col(1, "G", "uniqueidentifier", 16, rowGuidCol: true);
        var plain = Col(1, "G", "uniqueidentifier", 16);

        Assert.Contains("ALTER COLUMN [G] ADD ROWGUIDCOL;", Script(Build(Table(guid)), Build(Table(plain))));
        Assert.Contains("ALTER COLUMN [G] DROP ROWGUIDCOL;", Script(Build(Table(plain)), Build(Table(guid))));
    }

    [Fact]
    public void Retyped_sparse_column_carries_sparse_in_the_alter_and_is_not_added_twice()
    {
        // Tip de değişiyorsa SPARSE o ifadenin parçası olur; ayrıca ADD SPARSE üretilmemeli.
        // (Aynı tip içinde genişletme seçildi: tip DEĞİŞİMİ veri kaybı sayılıp gated'a düşerdi.)
        var source = Build(Table(Col(1, "C", "varchar", 20, sparse: true)));
        var target = Build(Table(Col(1, "C", "varchar", 10)));

        var script = Script(source, target);

        Assert.Contains("ALTER COLUMN [C] varchar(20) NULL SPARSE;", script);
        Assert.DoesNotContain("ADD SPARSE", script);
    }

    [Fact]
    public void Dropping_sparse_while_retyping_still_emits_drop_sparse()
    {
        // Yazmamak "kaldır" demek değildir: kapatma açıkça ifade edilmeli.
        var source = Build(Table(Col(1, "C", "varchar", 20)));
        var target = Build(Table(Col(1, "C", "varchar", 10, sparse: true)));

        var script = Script(source, target);

        Assert.Contains("ALTER COLUMN [C] varchar(20) NULL;", script);
        Assert.Contains("DROP SPARSE;", script);
    }

    [Fact]
    public void Filestream_change_is_reported_as_skipped_not_scripted()
    {
        // FILESTREAM ALTER ile açılıp kapatılamaz — yarım SQL üretmektense açık uyarı.
        var result = TableScriptGenerator.Generate(
            SchemaComparer.Compare(
                Build(Table(Col(1, "D", "varbinary", -1, fileStream: true))),
                Build(Table(Col(1, "D", "varbinary", -1)))),
            null, TableScriptOptions.Default);

        Assert.DoesNotContain("FILESTREAM", result.Sql);
        Assert.Contains(result.Skipped, s => s.Reason.Contains("FILESTREAM", StringComparison.Ordinal));
    }

    [Fact]
    public void Column_set_change_is_reported_as_skipped_not_scripted()
    {
        var result = TableScriptGenerator.Generate(
            SchemaComparer.Compare(
                Build(Table(Col(1, "S", "xml", -1, columnSet: true))),
                Build(Table(Col(1, "S", "xml", -1)))),
            null, TableScriptOptions.Default);

        Assert.Contains(result.Skipped, s => s.Reason.Contains("COLUMN_SET", StringComparison.Ordinal));
    }

    // --- arayüz ---

    [Fact]
    public void Change_catalog_names_the_attribute_that_changed()
    {
        var result = SchemaComparer.Compare(Build(Table(Col(1, "C", sparse: true))), Build(Table(Col(1, "C"))));
        var change = Assert.Single(ChangeCatalog.Build(result));
        var child = Assert.Single(change.Children, c => c.Category == "Columns");

        Assert.Contains("SPARSE", child.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void Coverage_probe_marks_storage_attributes_as_covered()
    {
        var probe = Assert.Single(CoverageProbe.Probes, p => p.Item.StartsWith("Sparse /", StringComparison.Ordinal));
        Assert.True(probe.Covered);
    }
}
