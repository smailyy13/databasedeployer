using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

public class RoleScriptGeneratorTests
{
    private static CatalogSet RoleCatalog(int roleId, string name, params string[] members)
    {
        return new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Roles = [new RoleRow(roleId, name, "dbo")],
            RoleMembers = [.. members.Select(m => new RoleMemberRow(roleId, m))],
        };
    }

    private static CatalogSet Empty() => new() { DatabaseName = "test", ServerName = "TESTSRV" };

    private static DatabaseSnapshot Build(CatalogSet c) =>
        SnapshotBuilder.Build(c, new ExtractionReport(), SnapshotOptions.Default);

    private static RoleScriptResult Generate(DatabaseSnapshot source, DatabaseSnapshot target)
    {
        var result = SchemaComparer.Compare(source, target);
        return RoleScriptGenerator.Generate(result, selection: null, RoleScriptOptions.Default);
    }

    [Fact]
    public void Added_role_emits_create_role()
    {
        var script = Generate(Build(RoleCatalog(50, "Reader")), Build(Empty()));

        Assert.Contains("CREATE ROLE [Reader];", script.Sql);
        Assert.Contains(new ObjectKey("", "Reader", ObjectKind.Role), script.Included);
    }

    [Fact]
    public void Added_role_with_members_emits_add_member_guarded()
    {
        var script = Generate(Build(RoleCatalog(50, "Reader", "AppUser")), Build(Empty()));

        Assert.Contains("CREATE ROLE [Reader];", script.Sql);
        Assert.Contains("ALTER ROLE [Reader] ADD MEMBER [AppUser];", script.Sql);
        Assert.Contains("DATABASE_PRINCIPAL_ID(N'AppUser')", script.Sql);
    }

    [Fact]
    public void Removed_role_is_dropped_with_guard()
    {
        var script = Generate(Build(Empty()), Build(RoleCatalog(50, "Legacy", "OldUser")));

        Assert.Contains("DROP ROLE [Legacy];", script.Sql);
        Assert.Contains("ALTER ROLE [Legacy] DROP MEMBER [OldUser];", script.Sql);
        Assert.Contains("DATABASE_PRINCIPAL_ID(N'Legacy') IS NOT NULL", script.Sql);
    }

    [Fact]
    public void Removed_role_is_kept_when_drops_disabled()
    {
        var result = SchemaComparer.Compare(Build(Empty()), Build(RoleCatalog(50, "Legacy")));
        var script = RoleScriptGenerator.Generate(result, null, new RoleScriptOptions { IncludeDrops = false });

        Assert.DoesNotContain("DROP ROLE", script.Sql);
        Assert.Contains(script.Skipped, s => s.Key.Name == "Legacy");
    }

    [Fact]
    public void Membership_change_adds_and_drops_members()
    {
        var source = Build(RoleCatalog(50, "Reader", "KeepUser", "NewUser"));
        var target = Build(RoleCatalog(50, "Reader", "KeepUser", "OldUser"));

        var script = Generate(source, target);

        Assert.Contains("ALTER ROLE [Reader] ADD MEMBER [NewUser];", script.Sql);
        Assert.Contains("ALTER ROLE [Reader] DROP MEMBER [OldUser];", script.Sql);
        Assert.DoesNotContain("KeepUser", script.Sql);
    }

    [Fact]
    public void No_role_differences_produce_empty_script()
    {
        var role = RoleCatalog(50, "Reader", "AppUser");
        var script = Generate(Build(role), Build(RoleCatalog(50, "Reader", "AppUser")));

        Assert.True(script.IsEmpty);
        Assert.Equal(string.Empty, script.Sql);
    }

    [Fact]
    public void Selection_excludes_unselected_roles()
    {
        var source = Build(new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Roles = [new RoleRow(50, "Wanted", "dbo"), new RoleRow(51, "Ignored", "dbo")],
        });
        var target = Build(Empty());
        var result = SchemaComparer.Compare(source, target);
        var selection = new HashSet<ObjectKey>(ObjectKeyComparer.CaseInsensitive)
        {
            new("", "Wanted", ObjectKind.Role),
        };

        var script = RoleScriptGenerator.Generate(result, selection, RoleScriptOptions.Default);

        Assert.Contains("CREATE ROLE [Wanted];", script.Sql);
        Assert.DoesNotContain("Ignored", script.Sql);
    }
}
