using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Extended property'lerin obje/kolon/şema/db dışındaki sınıfları:
/// parametre (class 2), principal (class 4), index (class 7).
///
/// Üçü de host objenin parçasıdır — ayrı obje değil. Kimlikleri ADLA kurulur, id ile değil:
/// principal_id ve index_id ortamlar arasında farklıdır, id kıyaslansa her karşılaştırma
/// sahte farkla dolardı.
/// </summary>
public class ExtendedPropertyScopeTests
{
    private const int ProcId = 300;
    private const int TableId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static CatalogSet Catalog(
        List<ExtendedPropertyRow>? properties = null,
        int principalId = 10,
        int indexId = 2) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects =
        [
            new ObjectRow(TableId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
            new ObjectRow(ProcId, "dbo", "GetCustomer", "P", DateTime.UnixEpoch, 0),
        ],
        Modules = [new ModuleRow(ProcId, "CREATE PROCEDURE dbo.GetCustomer @Id int AS SELECT 1", true, true)],
        Columns = [new ColumnRow(TableId, 1, "Id", "sys", "int", 4, 10, 0, true, null, false, false,
            null, null, null, null, null, null, null)],
        Indexes = [new IndexRow(TableId, indexId, "IX_Customer_Id", "NONCLUSTERED", false, false, false, 0, false, false, null)],
        IndexColumns = [new IndexColumnRow(TableId, indexId, 1, 1, 1, false, false)],
        Parameters = [new ParameterRow(ProcId, 1, "@Id")],
        Users = [new UserRow("AppUser", "S", "dbo", principalId)],
        ExtendedProperties = properties ?? [],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Canonical(DatabaseSnapshot db, ObjectKey key) =>
        db.Objects[key].PartCanonical.GetValueOrDefault("extendedProperties", string.Empty);

    private static readonly ObjectKey ProcKey = new("dbo", "GetCustomer", ObjectKind.Procedure);
    private static readonly ObjectKey TableKey = new("dbo", "Customer", ObjectKind.Table);
    private static readonly ObjectKey UserKey = new("", "AppUser", ObjectKind.User);

    // --- parametre (class 2) ---

    [Fact]
    public void Parameter_property_is_attached_to_its_module_with_the_parameter_name()
    {
        var db = Build(Catalog([new ExtendedPropertyRow(2, ProcId, 1, "MS_Description", "müşteri no")]));

        Assert.Contains("ep|param:@Id|MS_Description=müşteri no", Canonical(db, ProcKey), StringComparison.Ordinal);
    }

    [Fact]
    public void Parameter_property_difference_is_detected()
    {
        var withProperty = Build(Catalog([new ExtendedPropertyRow(2, ProcId, 1, "MS_Description", "eski")]));
        var changed = Build(Catalog([new ExtendedPropertyRow(2, ProcId, 1, "MS_Description", "yeni")]));

        Assert.Single(SchemaComparer.Compare(changed, withProperty).Differences);
    }

    [Fact]
    public void Parameter_property_is_scripted_with_level2_parameter()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(2, ProcId, 1, "MS_Description", "müşteri no")]));
        var script = ExtendedPropertyScriptGenerator.Generate(
            SchemaComparer.Compare(source, Build(Catalog())), null, null);

        Assert.Contains("@level2type = N'PARAMETER', @level2name = N'@Id'", script.Sql);
    }

    // --- index (class 7) ---

    [Fact]
    public void Index_property_is_attached_to_its_table_with_the_index_name()
    {
        var db = Build(Catalog([new ExtendedPropertyRow(7, TableId, 2, "MS_Description", "arama index'i")]));

        Assert.Contains("ep|index:IX_Customer_Id|", Canonical(db, TableKey), StringComparison.Ordinal);
    }

    [Fact]
    public void Index_property_is_compared_by_name_not_index_id()
    {
        // index_id ortamlar arasında farklı olabilir; ad aynıysa fark OLMAMALI.
        var left = Build(Catalog([new ExtendedPropertyRow(7, TableId, 2, "MS_Description", "x")], indexId: 2));
        var right = Build(Catalog([new ExtendedPropertyRow(7, TableId, 5, "MS_Description", "x")], indexId: 5));

        Assert.Empty(SchemaComparer.Compare(left, right).Differences);
    }

    [Fact]
    public void Index_property_is_scripted_with_level2_index()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(7, TableId, 2, "MS_Description", "arama")]));
        var script = ExtendedPropertyScriptGenerator.Generate(
            SchemaComparer.Compare(source, Build(Catalog())), null, null);

        Assert.Contains("@level2type = N'INDEX', @level2name = N'IX_Customer_Id'", script.Sql);
    }

    // --- principal (class 4) ---

    [Fact]
    public void Principal_property_is_attached_to_the_user()
    {
        var db = Build(Catalog([new ExtendedPropertyRow(4, 10, 0, "MS_Description", "uygulama kullanıcısı")]));

        Assert.Contains("ep|principal|MS_Description=", Canonical(db, UserKey), StringComparison.Ordinal);
    }

    [Fact]
    public void Principal_property_is_compared_by_name_not_principal_id()
    {
        // principal_id her ortamda farklıdır; id kıyaslansaydı her kullanıcı "değişti" görünürdü.
        var left = Build(Catalog([new ExtendedPropertyRow(4, 10, 0, "MS_Description", "x")], principalId: 10));
        var right = Build(Catalog([new ExtendedPropertyRow(4, 77, 0, "MS_Description", "x")], principalId: 77));

        Assert.Empty(SchemaComparer.Compare(left, right).Differences);
    }

    [Fact]
    public void Unresolvable_principal_property_is_skipped_rather_than_guessed()
    {
        // Principal bulunamazsa property atlanır; uydurulan bir host sahte fark üretirdi.
        var db = Build(Catalog([new ExtendedPropertyRow(4, 999, 0, "MS_Description", "?")]));

        Assert.DoesNotContain(db.Objects.Values, o =>
            o.PartCanonical.GetValueOrDefault("extendedProperties", string.Empty).Contains("principal", StringComparison.Ordinal));
    }

    [Fact]
    public void Principal_property_is_scripted_with_user_level0()
    {
        var source = Build(Catalog([new ExtendedPropertyRow(4, 10, 0, "MS_Description", "uygulama")]));
        var script = ExtendedPropertyScriptGenerator.Generate(
            SchemaComparer.Compare(source, Build(Catalog())), null, null);

        Assert.Contains("@level0type = N'USER', @level0name = N'AppUser'", script.Sql);
        Assert.DoesNotContain("@level1type", script.Sql);
    }

    // --- kapsam ---

    [Fact]
    public void Coverage_probe_marks_the_new_classes_as_covered()
    {
        var probe = Assert.Single(CoverageProbe.Probes, p => p.Item.Contains("parametre/principal/index", StringComparison.Ordinal));
        Assert.True(probe.Covered);
    }

    [Fact]
    public void Remaining_property_classes_are_still_reported_as_gaps()
    {
        var probe = Assert.Single(CoverageProbe.Probes, p => p.Item.Contains("öteki sınıflar", StringComparison.Ordinal));
        Assert.False(probe.Covered);
    }
}
