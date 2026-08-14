using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

/// <summary>
/// Roller, rol üyelikleri ve obje/şema izinleri gerçek build hattından geçirilerek test
/// edilir. SnapshotBuilder internal; InternalsVisibleTo ile erişilebilir.
/// </summary>
public class PermissionTests
{
    private const int CustomerId = 100;
    private const int ReaderRoleId = 50;

    private static CatalogSet Catalog(
        IEnumerable<RoleRow>? roles = null,
        IEnumerable<RoleMemberRow>? members = null,
        IEnumerable<PermissionRow>? permissions = null,
        IEnumerable<ColumnRow>? columns = null,
        IEnumerable<UserRow>? users = null)
    {
        return new CatalogSet
        {
            DatabaseName = "test",
            ServerName = "TESTSRV",
            Schemas = [new SchemaRow(5, "sales")],
            Objects = [new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0)],
            Columns = [.. columns ?? []],
            Roles = [.. roles ?? []],
            RoleMembers = [.. members ?? []],
            Permissions = [.. permissions ?? []],
            Users = [.. users ?? []],
        };
    }

    private static DatabaseSnapshot Build(CatalogSet catalog, SnapshotOptions? options = null) =>
        SnapshotBuilder.Build(catalog, new ExtractionReport(), options ?? SnapshotOptions.Default);

    [Fact]
    public void Role_only_in_source_is_Added()
    {
        var source = Build(Catalog(roles: [new RoleRow(ReaderRoleId, "Reader", "dbo")]));
        var target = Build(Catalog());

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences, d => d.Key.Kind == ObjectKind.Role);
        Assert.Equal(DiffKind.Added, diff.Kind);
        Assert.Equal("Reader", diff.Key.Name);
    }

    [Fact]
    public void Role_membership_difference_makes_role_Changed()
    {
        var role = new RoleRow(ReaderRoleId, "Reader", "dbo");
        var source = Build(Catalog(roles: [role], members: [new RoleMemberRow(ReaderRoleId, "AppUser")]));
        var target = Build(Catalog(roles: [role]));

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences, d => d.Key.Kind == ObjectKind.Role);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("members", diff.ChangedParts);

        var change = Assert.Single(ChangeCatalog.Build(result), c => c.ObjectType == "Role");
        var child = Assert.Single(change.Children, c => c.Category == "Membership");
        Assert.Equal(ChangeAction.Add, child.Action);
        Assert.Equal("AppUser", child.Name);
    }

    [Fact]
    public void Identical_roles_produce_no_difference()
    {
        var role = new RoleRow(ReaderRoleId, "Reader", "dbo");
        var member = new RoleMemberRow(ReaderRoleId, "AppUser");
        var source = Build(Catalog(roles: [role], members: [member]));
        var target = Build(Catalog(roles: [role], members: [member]));

        var result = SchemaComparer.Compare(source, target);

        Assert.DoesNotContain(result.Differences, d => d.Key.Kind == ObjectKind.Role);
    }

    [Fact]
    public void Object_permission_added_makes_host_changed()
    {
        var source = Build(Catalog(
            permissions: [new PermissionRow(1, CustomerId, 0, "SELECT", "GRANT", "Reader")]));
        var target = Build(Catalog());

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences, d => d.Key.Kind == ObjectKind.Table);
        Assert.Equal(DiffKind.Changed, diff.Kind);
        Assert.Contains("permissions", diff.ChangedParts);

        var change = Assert.Single(ChangeCatalog.Build(result), c => c.ObjectType == "Table");
        var child = Assert.Single(change.Children, c => c.Category == "Permissions");
        Assert.Equal(ChangeAction.Add, child.Action);
        Assert.Contains("SELECT", child.Name);
    }

    [Fact]
    public void Deny_and_grant_are_distinct_permissions()
    {
        var source = Build(Catalog(
            permissions: [new PermissionRow(1, CustomerId, 0, "SELECT", "DENY", "Reader")]));
        var target = Build(Catalog(
            permissions: [new PermissionRow(1, CustomerId, 0, "SELECT", "GRANT", "Reader")]));

        var result = SchemaComparer.Compare(source, target);
        var change = Assert.Single(ChangeCatalog.Build(result), c => c.ObjectType == "Table");

        // GRANT hedefte var kaynakta yok → silinecek; DENY kaynakta var hedefte yok → eklenecek.
        Assert.Contains(change.Children, c => c.Category == "Permissions" && c.Action == ChangeAction.Add);
        Assert.Contains(change.Children, c => c.Category == "Permissions" && c.Action == ChangeAction.Delete);
    }

    [Fact]
    public void Schema_permission_attaches_to_schema()
    {
        var source = Build(Catalog(
            permissions: [new PermissionRow(3, 5, 0, "EXECUTE", "GRANT", "Reader")]));
        var target = Build(Catalog());

        var result = SchemaComparer.Compare(source, target);

        var diff = Assert.Single(result.Differences, d => d.Key.Kind == ObjectKind.Schema && d.Key.Name == "sales");
        Assert.Contains("permissions", diff.ChangedParts);
    }

    [Fact]
    public void Column_permission_is_scoped_to_the_column()
    {
        var cols = new[] { Column(1, "Id"), Column(2, "Email") };
        var source = Build(Catalog(
            permissions: [new PermissionRow(1, CustomerId, 2, "SELECT", "GRANT", "Reader")],
            columns: cols));
        var target = Build(Catalog(columns: cols));

        var result = SchemaComparer.Compare(source, target);
        var change = Assert.Single(ChangeCatalog.Build(result), c => c.ObjectType == "Table");
        var child = Assert.Single(change.Children, c => c.Category == "Permissions");
        Assert.Contains("Email", child.Name);
    }

    [Fact]
    public void IgnorePermissions_suppresses_roles_and_permissions()
    {
        var options = SnapshotOptions.Default with { IgnorePermissions = true };
        var source = Build(Catalog(
            roles: [new RoleRow(ReaderRoleId, "Reader", "dbo")],
            permissions: [new PermissionRow(1, CustomerId, 0, "SELECT", "GRANT", "Reader")]), options);
        var target = Build(Catalog(), options);

        var result = SchemaComparer.Compare(source, target);

        Assert.Empty(result.Differences);
    }

    // Kullanıcı da (users) IgnorePermissions ile bastırılır — ne diff'e ne script'e girer.
    [Fact]
    public void IgnorePermissions_suppresses_users()
    {
        var options = SnapshotOptions.Default with { IgnorePermissions = true };
        var source = Build(Catalog(users: [new UserRow(@"KUVEYTTURK\dcandan", "E", "dbo")]), options);
        var target = Build(Catalog(), options);

        var result = SchemaComparer.Compare(source, target);

        Assert.Empty(result.Differences);
        Assert.DoesNotContain(source.Objects.Keys, k => k.Kind == ObjectKind.User);
    }

    // Kapsam kontrolü: IgnorePermissions AÇIK iken users + roller + izinler snapshot'a HİÇ girmez;
    // KAPALI iken hepsi görünür (varsayılan davranışın karşıt ucu).
    [Fact]
    public void IgnorePermissions_toggles_entire_security_layer()
    {
        var cat = Catalog(
            roles: [new RoleRow(ReaderRoleId, "Reader", "dbo")],
            members: [new RoleMemberRow(ReaderRoleId, @"KUVEYTTURK\team")],
            users: [new UserRow(@"KUVEYTTURK\dcandan", "E", "dbo")],
            permissions: [new PermissionRow(0, 0, 0, "CONNECT", "GRANT", @"KUVEYTTURK\team")]);

        var ignored = Build(cat, SnapshotOptions.Default with { IgnorePermissions = true });
        Assert.DoesNotContain(ignored.Objects.Keys, k => k.Kind is ObjectKind.User or ObjectKind.Role);

        var kept = Build(cat, SnapshotOptions.Default);   // IgnorePermissions = false
        Assert.Contains(kept.Objects.Keys, k => k.Kind == ObjectKind.User);
        Assert.Contains(kept.Objects.Keys, k => k.Kind == ObjectKind.Role);
    }

    // Sentetik "(database)" objesi artık okunur T-SQL DisplayScript üretmeli — alt panelde
    // ham kanonik (perm|database|...) yerine gerçek GRANT/DENY görünsün.
    [Fact]
    public void Database_level_permissions_render_as_tsql_display_script()
    {
        var cat = Catalog(permissions:
        [
            new PermissionRow(0, 0, 0, "CONNECT", "GRANT", "DevopsRapor"),
            new PermissionRow(0, 0, 0, "EXECUTE", "DENY", "sudbkubetest"),
            new PermissionRow(0, 0, 0, "VIEW DEFINITION", "GRANT_WITH_GRANT_OPTION", @"KUVEYTTURK\aaktas"),
        ]);
        var snap = Build(cat, SnapshotOptions.Default with { KeepDisplayScripts = true });

        var db = snap.Objects[new ObjectKey(string.Empty, "(database)", ObjectKind.Database)];
        var script = db.DisplayScript!;

        Assert.Contains("GRANT CONNECT TO [DevopsRapor];", script);
        Assert.Contains("DENY EXECUTE TO [sudbkubetest];", script);
        Assert.Contains(@"GRANT VIEW DEFINITION TO [KUVEYTTURK\aaktas] WITH GRANT OPTION;", script);
        Assert.DoesNotContain("perm|database|", script);   // ham kanonik OLMAMALI
    }

    private static ColumnRow Column(int columnId, string name) => new(
        CustomerId, columnId, name, "sys", "int", 4, 10, 0,
        true, null, false, false, null, null, null, null, null, null, null);
}
