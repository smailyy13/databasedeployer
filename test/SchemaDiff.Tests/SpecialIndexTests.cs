using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// XML ve spatial index'ler. Bunlar `sys.indexes`'te durdukları için GENEL index yoluna
/// düşüyorlardı ve <c>CREATE XML INDEX [x] ON t ([col] ASC)</c> gibi GEÇERSİZ SQL üretiliyordu:
/// XML index'te ASC/DESC yoktur, primary'de PRIMARY, secondary'de USING XML INDEX zorunludur.
/// Bu yüzden artık genel sorgudan dışlanıp kendi yollarından geçiyorlar.
/// </summary>
public class SpecialIndexTests
{
    private const int DocId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name, string type = "xml") => new(
        DocId, id, name, "sys", type, -1, 0, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    private static IndexColumnRow Key(int indexId, int columnId) =>
        new(DocId, indexId, columnId, columnId, 1, false, false);

    private static XmlIndexRow Primary(string name = "PXML_Doc", int indexId = 2) =>
        new(DocId, indexId, name, null, null, null);

    private static XmlIndexRow Secondary(
        string name = "SXML_Doc_Path", int indexId = 3, string forType = "PATH", string primary = "PXML_Doc") =>
        new(DocId, indexId, name, 2, forType, primary);

    private static SpatialIndexRow Spatial(
        string name = "SIX_Doc_Geo", int indexId = 4, string scheme = "GEOMETRY_GRID",
        double? xmin = 0, double? ymin = 0, double? xmax = 100, double? ymax = 100,
        string? level1 = "MEDIUM", int? cells = 16) =>
        new(DocId, indexId, name, scheme, xmin, ymin, xmax, ymax,
            level1, level1 is null ? null : "MEDIUM", level1 is null ? null : "MEDIUM",
            level1 is null ? null : "MEDIUM", cells);

    private static CatalogSet Table(
        List<XmlIndexRow>? xml = null,
        List<SpatialIndexRow>? spatial = null,
        List<IndexColumnRow>? indexColumns = null,
        List<IndexRow>? indexes = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects = [new ObjectRow(DocId, "dbo", "Doc", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Payload"), Col(2, "Shape", "geometry")],
        XmlIndexes = xml ?? [],
        SpatialIndexes = spatial ?? [],
        Indexes = indexes ?? [],
        IndexColumns = indexColumns ?? [],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(CatalogSet source, CatalogSet target) =>
        TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(source), Build(target)), null, TableScriptOptions.Default).Sql;

    private static CatalogSet WithPrimaryXml() => Table([Primary()], indexColumns: [Key(2, 1)]);

    // --- XML index ---

    [Fact]
    public void Primary_xml_index_uses_primary_keyword_and_no_sort_direction()
    {
        var script = Script(WithPrimaryXml(), Table());

        Assert.Contains("CREATE PRIMARY XML INDEX [PXML_Doc] ON [dbo].[Doc] ([Payload]);", script);
        Assert.DoesNotContain("[Payload] ASC", script);
    }

    [Fact]
    public void Secondary_xml_index_references_its_primary()
    {
        var source = Table([Primary(), Secondary()], indexColumns: [Key(2, 1), Key(3, 1)]);
        var script = Script(source, Table());

        Assert.Contains(
            "CREATE XML INDEX [SXML_Doc_Path] ON [dbo].[Doc] ([Payload]) " +
            "USING XML INDEX [PXML_Doc] FOR PATH;", script);
    }

    [Fact]
    public void Primary_is_created_before_secondary()
    {
        // Secondary primary'ye bağlı: ters sırada CREATE patlar.
        var source = Table([Secondary(), Primary()], indexColumns: [Key(2, 1), Key(3, 1)]);
        var script = Script(source, Table());

        var primary = script.IndexOf("CREATE PRIMARY XML INDEX", StringComparison.Ordinal);
        var secondary = script.IndexOf("CREATE XML INDEX", StringComparison.Ordinal);

        Assert.True(primary >= 0 && secondary > primary, "primary önce gelmeli");
    }

    [Fact]
    public void Secondary_is_dropped_before_primary()
    {
        var target = Table([Primary(), Secondary()], indexColumns: [Key(2, 1), Key(3, 1)]);
        var script = Script(Table(), target);

        var secondary = script.IndexOf("DROP INDEX [SXML_Doc_Path]", StringComparison.Ordinal);
        var primary = script.IndexOf("DROP INDEX [PXML_Doc]", StringComparison.Ordinal);

        Assert.True(secondary >= 0 && primary > secondary, "secondary önce düşmeli");
    }

    [Fact]
    public void Xml_index_is_not_emitted_through_the_generic_index_path()
    {
        // Regresyon kilidi: eskiden "CREATE XML INDEX ... ([Payload] ASC)" üretiliyordu.
        var script = Script(WithPrimaryXml(), Table());

        Assert.DoesNotContain("CREATE NONCLUSTERED INDEX", script);
        Assert.DoesNotContain("ASC)", script);
    }

    [Fact]
    public void Secondary_type_difference_recreates_the_index()
    {
        var path = Table([Primary(), Secondary(forType: "PATH")], indexColumns: [Key(2, 1), Key(3, 1)]);
        var value = Table([Primary(), Secondary(forType: "VALUE")], indexColumns: [Key(2, 1), Key(3, 1)]);

        var script = Script(path, value);

        Assert.Contains("DROP INDEX [SXML_Doc_Path]", script);
        Assert.Contains("FOR PATH;", script);
    }

    [Fact]
    public void Xml_index_difference_is_reported_on_its_own_part()
    {
        var diff = Assert.Single(SchemaComparer.Compare(Build(WithPrimaryXml()), Build(Table())).Differences);

        Assert.Contains("xmlIndexes", diff.ChangedParts);
    }

    [Fact]
    public void Orphan_secondary_without_a_readable_primary_is_skipped()
    {
        // Primary adı okunamazsa USING yazılamaz; yarım script üretmektense atlanır.
        var orphan = new XmlIndexRow(DocId, 3, "SXML_Orphan", 99, null, null);
        var snapshot = Build(Table([orphan], indexColumns: [Key(3, 1)]));

        Assert.False(snapshot.Objects[TestFactory.Table("Doc")].Parts.ContainsKey("xmlIndexes"));
    }

    // --- spatial index ---

    [Fact]
    public void Geometry_index_writes_bounding_box_and_grids()
    {
        var script = Script(Table(spatial: [Spatial()], indexColumns: [Key(4, 2)]), Table());

        Assert.Contains(
            "CREATE SPATIAL INDEX [SIX_Doc_Geo] ON [dbo].[Doc] ([Shape]) USING GEOMETRY_GRID " +
            "WITH (BOUNDING_BOX = (0, 0, 100, 100), " +
            "GRIDS = (LEVEL_1 = MEDIUM, LEVEL_2 = MEDIUM, LEVEL_3 = MEDIUM, LEVEL_4 = MEDIUM), " +
            "CELLS_PER_OBJECT = 16);", script);
    }

    [Fact]
    public void Geography_index_has_no_bounding_box()
    {
        // BOUNDING_BOX yalnızca GEOMETRY içindir; GEOGRAPHY'de yazmak hatadır.
        var geography = Spatial(scheme: "GEOGRAPHY_GRID");
        var script = Script(Table(spatial: [geography], indexColumns: [Key(4, 2)]), Table());

        Assert.Contains("USING GEOGRAPHY_GRID", script);
        Assert.DoesNotContain("BOUNDING_BOX", script);
    }

    [Fact]
    public void Auto_grid_index_omits_grid_levels()
    {
        // AUTO_GRID'de seviyeleri SQL Server seçer; yazmak hatadır.
        var auto = Spatial(scheme: "GEOMETRY_AUTO_GRID");
        var script = Script(Table(spatial: [auto], indexColumns: [Key(4, 2)]), Table());

        Assert.Contains("USING GEOMETRY_AUTO_GRID", script);
        Assert.DoesNotContain("GRIDS", script);
        Assert.Contains("BOUNDING_BOX", script);
    }

    [Fact]
    public void Fractional_coordinates_use_invariant_decimal_separator()
    {
        // Türkçe kültürde "0,5" yazılsa SQL bozulurdu.
        var fractional = Spatial(xmin: -0.5, ymin: 0.25, xmax: 10.75, ymax: 20.125);
        var script = Script(Table(spatial: [fractional], indexColumns: [Key(4, 2)]), Table());

        Assert.Contains("BOUNDING_BOX = (-0.5, 0.25, 10.75, 20.125)", script);
    }

    [Fact]
    public void Spatial_difference_is_reported_on_its_own_part()
    {
        var diff = Assert.Single(SchemaComparer.Compare(
            Build(Table(spatial: [Spatial()], indexColumns: [Key(4, 2)])), Build(Table())).Differences);

        Assert.Contains("spatialIndexes", diff.ChangedParts);
    }

    [Fact]
    public void Cells_per_object_difference_recreates_the_index()
    {
        var sixteen = Table(spatial: [Spatial(cells: 16)], indexColumns: [Key(4, 2)]);
        var thirtyTwo = Table(spatial: [Spatial(cells: 32)], indexColumns: [Key(4, 2)]);

        var script = Script(sixteen, thirtyTwo);

        Assert.Contains("DROP INDEX [SIX_Doc_Geo]", script);
        Assert.Contains("CELLS_PER_OBJECT = 16", script);
    }

    // --- yeni tablo ---

    [Fact]
    public void New_table_script_contains_special_indexes_in_their_own_batches()
    {
        var snapshot = Build(Table([Primary(), Secondary()], [Spatial()],
            [Key(2, 1), Key(3, 1), Key(4, 2)])).Objects[TestFactory.Table("Doc")];

        var script = snapshot.DisplayScript!;
        var table = script.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        var primary = script.IndexOf("CREATE PRIMARY XML INDEX", StringComparison.Ordinal);
        var spatial = script.IndexOf("CREATE SPATIAL INDEX", StringComparison.Ordinal);

        Assert.True(table < primary && primary < spatial);
        Assert.Contains("GO", script);
    }

    // --- kapsam ---

    [Fact]
    public void Coverage_probe_marks_special_indexes_as_covered()
    {
        foreach (var item in new[] { "XML index'ler", "Spatial index'ler" })
            Assert.True(Assert.Single(CoverageProbe.Probes, p => p.Item == item).Covered, item);
    }

    [Fact]
    public void Change_catalog_lists_them_under_their_own_folders()
    {
        var result = SchemaComparer.Compare(
            Build(Table([Primary()], [Spatial()], [Key(2, 1), Key(4, 2)])), Build(Table()));
        var change = Assert.Single(ChangeCatalog.Build(result));

        Assert.Contains(change.Children, c => c.Category == "XML Indexes");
        Assert.Contains(change.Children, c => c.Category == "Spatial Indexes");
    }
}
