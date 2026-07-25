using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

public class PermissionScriptGeneratorTests
{
    private const int CustomerId = 100;

    private static CatalogSet Catalog(IEnumerable<PermissionRow> perms, params ColumnRow[] columns) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Schemas = [new SchemaRow(5, "sales")],
        Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
        Columns = [.. columns],
        Permissions = [.. perms],
    };

    private static ColumnRow Col(int id, string name) => new(
        CustomerId, id, name, "sys", "int", 4, 10, 0, true, null, false, false, null, null, null, null, null, null, null);

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), SnapshotOptions.Default);

    private static PermissionScriptResult Generate(DatabaseSnapshot source, DatabaseSnapshot target) =>
        PermissionScriptGenerator.Generate(SchemaComparer.Compare(source, target));

    [Fact]
    public void Added_grant_is_emitted_with_object_target()
    {
        var source = Build(Catalog([new PermissionRow(1, CustomerId, 0, "SELECT", "GRANT", "Reader")]));
        var target = Build(Catalog([]));

        var script = Generate(source, target);

        Assert.Contains("GRANT SELECT ON OBJECT::[dbo].[Customer] TO [Reader];", script.Sql);
        Assert.Contains("DATABASE_PRINCIPAL_ID(N'Reader')", script.Sql);
    }

    [Fact]
    public void Removed_grant_is_revoked()
    {
        var source = Build(Catalog([]));
        var target = Build(Catalog([new PermissionRow(1, CustomerId, 0, "EXECUTE", "GRANT", "Reader")]));

        var script = Generate(source, target);

        Assert.Contains("REVOKE EXECUTE ON OBJECT::[dbo].[Customer] FROM [Reader];", script.Sql);
    }

    [Fact]
    public void Deny_uses_deny_verb()
    {
        var source = Build(Catalog([new PermissionRow(1, CustomerId, 0, "SELECT", "DENY", "Reader")]));
        var target = Build(Catalog([]));

        var script = Generate(source, target);
        Assert.Contains("DENY SELECT ON OBJECT::[dbo].[Customer] TO [Reader];", script.Sql);
    }

    [Fact]
    public void Grant_with_grant_option_is_emitted()
    {
        var source = Build(Catalog([new PermissionRow(1, CustomerId, 0, "SELECT", "GRANT_WITH_GRANT_OPTION", "Reader")]));
        var target = Build(Catalog([]));

        var script = Generate(source, target);
        Assert.Contains("WITH GRANT OPTION;", script.Sql);
    }

    [Fact]
    public void Grant_to_deny_flips_via_revoke_plus_deny()
    {
        var source = Build(Catalog([new PermissionRow(1, CustomerId, 0, "SELECT", "DENY", "Reader")]));
        var target = Build(Catalog([new PermissionRow(1, CustomerId, 0, "SELECT", "GRANT", "Reader")]));

        var script = Generate(source, target);
        Assert.Contains("REVOKE SELECT ON OBJECT::[dbo].[Customer] FROM [Reader];", script.Sql);
        Assert.Contains("DENY SELECT ON OBJECT::[dbo].[Customer] TO [Reader];", script.Sql);
    }

    [Fact]
    public void Column_permission_puts_column_in_parentheses()
    {
        var cols = new[] { Col(1, "Id"), Col(2, "Email") };
        var source = Build(Catalog([new PermissionRow(1, CustomerId, 2, "SELECT", "GRANT", "Reader")], cols));
        var target = Build(Catalog([], cols));

        var script = Generate(source, target);
        Assert.Contains("GRANT SELECT ([Email]) ON OBJECT::[dbo].[Customer] TO [Reader];", script.Sql);
    }

    [Fact]
    public void Schema_permission_uses_schema_target()
    {
        var source = Build(Catalog([new PermissionRow(3, 5, 0, "EXECUTE", "GRANT", "Reader")]));
        var target = Build(Catalog([]));

        var script = Generate(source, target);
        Assert.Contains("ON SCHEMA::[sales]", script.Sql);
    }

    [Fact]
    public void Multi_word_permission_name_is_parsed()
    {
        var source = Build(Catalog([new PermissionRow(1, CustomerId, 0, "VIEW DEFINITION", "GRANT", "Reader")]));
        var target = Build(Catalog([]));

        var script = Generate(source, target);
        Assert.Contains("GRANT VIEW DEFINITION ON OBJECT::[dbo].[Customer] TO [Reader];", script.Sql);
    }

    [Fact]
    public void Identical_permissions_produce_empty()
    {
        var p = new PermissionRow(1, CustomerId, 0, "SELECT", "GRANT", "Reader");
        var script = Generate(Build(Catalog([p])), Build(Catalog([p])));
        Assert.True(script.IsEmpty);
    }
}
