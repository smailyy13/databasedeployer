using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Script/dağıtım seçeneklerinin üretilen SQL'i gerçekten değiştirdiğini doğrular:
/// "Script validation for new constraints" (WITH CHECK / NOCHECK) ve "Drop objects not
/// in source" (DROP üret / üretme).
/// </summary>
public class ScriptOptionsTests
{
    private const int TableId = 100;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int id, string name) => new(
        TableId, id, name, "sys", "int", 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null);

    private static CatalogSet Table(List<CheckConstraintRow>? checks = null) => new()
    {
        DatabaseName = "t", ServerName = "s",
        Objects = [new ObjectRow(TableId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [Col(1, "Id")],
        CheckConstraints = checks ?? [],
    };

    private static CatalogSet Empty() => new() { DatabaseName = "t", ServerName = "s" };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(CatalogSet source, CatalogSet target, TableScriptOptions options) =>
        TableScriptGenerator.Generate(SchemaComparer.Compare(Build(source), Build(target)), null, options).Sql;

    private static List<CheckConstraintRow> Check() =>
        [new CheckConstraintRow(TableId, "CK_Pos", false, "([Id]>(0))", false, false)];

    // --- Script validation for new constraints ---

    [Fact]
    public void New_check_uses_WITH_CHECK_by_default()
    {
        var sql = Script(Table(Check()), Table(), TableScriptOptions.Default);
        Assert.Contains("WITH CHECK ADD CONSTRAINT [CK_Pos]", sql);
    }

    [Fact]
    public void New_check_uses_WITH_NOCHECK_when_validation_off()
    {
        var sql = Script(Table(Check()), Table(), TableScriptOptions.Default with { ValidateNewConstraints = false });
        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [CK_Pos]", sql);
        Assert.DoesNotContain("WITH CHECK ADD CONSTRAINT [CK_Pos]", sql);
    }

    // --- Drop objects not in source ---

    [Fact]
    public void Removed_table_dropped_when_option_on()
    {
        // Hedefte var, kaynakta yok → DROP (veri kaybı olduğu için AllowDataLoss de şart).
        var sql = Script(Empty(), Table(), TableScriptOptions.Default with { AllowDataLoss = true, IncludeDrops = true });
        Assert.Contains("DROP TABLE", sql);
    }

    [Fact]
    public void Removed_table_not_dropped_when_option_off()
    {
        var sql = Script(Empty(), Table(), TableScriptOptions.Default with { AllowDataLoss = true, IncludeDrops = false });
        Assert.DoesNotContain("DROP TABLE", sql);
    }
}
