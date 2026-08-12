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

    // --- kullanıcılar ve sabit-rol üyeliği (SSDT'nin yakaladığı boşluklar) ---

    private static CatalogSet UserCatalog(params (string Name, string Type, string? Schema)[] users) => new()
    {
        DatabaseName = "test", ServerName = "TESTSRV",
        Users = [.. users.Select(u => new UserRow(u.Name, u.Type, u.Schema))],
    };

    private static CatalogSet FixedRoleCatalog(int roleId, string name, params string[] members) => new()
    {
        DatabaseName = "test", ServerName = "TESTSRV",
        Roles = [new RoleRow(roleId, name, null, IsFixed: true)],
        RoleMembers = [.. members.Select(m => new RoleMemberRow(roleId, m))],
    };

    [Fact]
    public void Added_sql_user_emits_create_user_without_login()
    {
        var script = Generate(Build(UserCatalog(("Alice", "S", null))), Build(Empty()));
        Assert.Contains("CREATE USER [Alice] WITHOUT LOGIN;", script.Sql);
    }

    [Fact]
    public void Added_windows_user_has_no_without_login()
    {
        var script = Generate(Build(UserCatalog(("KUVEYTTURK\\grp", "G", null))), Build(Empty()));
        Assert.Contains("CREATE USER [KUVEYTTURK\\grp];", script.Sql);
        Assert.DoesNotContain("WITHOUT LOGIN", script.Sql);
    }

    [Fact]
    public void Removed_user_emits_drop_user()
    {
        var script = Generate(Build(Empty()), Build(UserCatalog(("Bob", "S", null))));
        Assert.Contains("DROP USER [Bob];", script.Sql);
        // Silmeden önce sahip olunan şemalar dbo'ya devredilir (Msg 15138'i önler).
        Assert.Contains("ALTER AUTHORIZATION ON SCHEMA::", script.Sql);
        Assert.Contains("DATABASE_PRINCIPAL_ID(N'Bob')", script.Sql);
    }

    [Fact]
    public void Section_commit_is_guarded_against_rollback()
    {
        // XACT_ABORT sonrası koşulsuz COMMIT Msg 3902 verir; @@TRANCOUNT koruması olmalı.
        var script = Generate(Build(RoleCatalog(50, "Reader")), Build(Empty()));
        Assert.Contains("IF @@TRANCOUNT > 0 COMMIT TRANSACTION;", script.Sql);
        Assert.DoesNotContain("\nCOMMIT TRANSACTION;", script.Sql);
    }

    [Fact]
    public void Create_user_guards_against_login_already_mapped()
    {
        // Kişisel kopyada login zaten dbo → SID guard'ı Msg 15063'ü önler.
        var script = Generate(Build(UserCatalog(("KUVEYTTURK\\dcandan", "U", null))), Build(Empty()));
        Assert.Contains("sid = SUSER_SID(N'KUVEYTTURK\\dcandan')", script.Sql);
    }

    [Fact]
    public void User_default_schema_change_emits_alter_user()
    {
        var script = Generate(Build(UserCatalog(("Alice", "S", "sales"))), Build(UserCatalog(("Alice", "S", "dbo"))));
        Assert.Contains("ALTER USER [Alice] WITH DEFAULT_SCHEMA=[sales];", script.Sql);
    }

    [Fact]
    public void Fixed_role_membership_change_is_detected()
    {
        // db_datareader (sabit) — hedefte Bob üye, kaynakta değil → üyelikten çıkar.
        var script = Generate(
            Build(FixedRoleCatalog(16384, "db_datareader")),
            Build(FixedRoleCatalog(16384, "db_datareader", "Bob")));
        Assert.Contains("ALTER ROLE [db_datareader] DROP MEMBER [Bob];", script.Sql);
    }

    [Fact]
    public void Create_user_precedes_drop_user_in_output()
    {
        // Sıra: yeni kullanıcı önce, silinen en son.
        var src = Build(UserCatalog(("Alice", "S", null)));
        var tgt = Build(UserCatalog(("Bob", "S", null)));
        var sql = Generate(src, tgt).Sql;
        Assert.True(sql.IndexOf("CREATE USER [Alice]", StringComparison.Ordinal)
                  < sql.IndexOf("DROP USER [Bob]", StringComparison.Ordinal));
    }
}
