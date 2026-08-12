using SchemaDiff.Core.Diff;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Scripting;

namespace SchemaDiff.Tests;

/// <summary>
/// Constraint DURUMU: pasif (disabled) ve güvenilmez (not trusted).
///
/// Bu ikisi karşılaştırılıyordu ama üretilen script'e yansımıyordu: kaynakta bilerek
/// kapatılmış bir CHECK, hedefte AKTİF olarak kuruluyordu — script farkı doğru görüp
/// yanlış tarafa taşıyordu. Üç hâlin T-SQL yazımı da birbirinden farklıdır.
/// </summary>
public class ConstraintStateTests
{
    private const int CustomerId = 100;
    private const int OrderId = 200;
    private static readonly SnapshotOptions WithScripts = SnapshotOptions.Default with { KeepDisplayScripts = true };

    private static ColumnRow Col(int objectId, int id, string name) => new(
        objectId, id, name, "sys", "int", 4, 10, 0, true, null, false, false,
        null, null, null, null, null, null, null);

    private static CheckConstraintRow Check(bool disabled = false, bool notTrusted = false, string definition = "([Id]>(0))") =>
        new(CustomerId, "CK_Positive", false, definition, disabled, notTrusted);

    private static ForeignKeyRow ForeignKey(bool disabled = false, bool notTrusted = false) =>
        new(900, OrderId, "FK_Order_Customer", false, CustomerId, 0, 0, disabled, notTrusted);

    private static CatalogSet Table(
        List<CheckConstraintRow>? checks = null, List<ForeignKeyRow>? foreignKeys = null) => new()
    {
        DatabaseName = "test",
        ServerName = "TESTSRV",
        Objects =
        [
            new ObjectRow(CustomerId, "dbo", "Customer", "U", DateTime.UnixEpoch, 0),
            new ObjectRow(OrderId, "dbo", "Order", "U", DateTime.UnixEpoch, 0),
        ],
        Columns = [Col(CustomerId, 1, "Id"), Col(OrderId, 1, "CustomerId")],
        CheckConstraints = checks ?? [],
        ForeignKeys = foreignKeys ?? [],
        ForeignKeyColumns = foreignKeys is { Count: > 0 }
            ? [new ForeignKeyColumnRow(900, 1, OrderId, 1, CustomerId, 1)]
            : [],
    };

    private static DatabaseSnapshot Build(CatalogSet c) => SnapshotBuilder.Build(c, new ExtractionReport(), WithScripts);

    private static string Script(CatalogSet source, CatalogSet target, TableScriptOptions? options = null) =>
        TableScriptGenerator.Generate(
            SchemaComparer.Compare(Build(source), Build(target)), null, options ?? TableScriptOptions.Default).Sql;

    // --- durum farkı, tanım aynı ---

    [Fact]
    public void Disabling_an_existing_check_uses_nocheck_constraint()
    {
        var script = Script(Table(checks: [Check(disabled: true)]), Table(checks: [Check()]));

        Assert.Contains("ALTER TABLE [dbo].[Customer] NOCHECK CONSTRAINT [CK_Positive];", script);
    }

    [Fact]
    public void State_only_change_does_not_drop_and_recreate()
    {
        // Tanım aynı: DROP + ADD hem gereksiz hem riskli (FK referansı varsa deploy patlar).
        var script = Script(Table(checks: [Check(disabled: true)]), Table(checks: [Check()]));

        Assert.DoesNotContain("DROP CONSTRAINT", script);
        Assert.DoesNotContain("ADD CONSTRAINT", script);
    }

    [Fact]
    public void Enabling_a_trusted_check_validates_existing_data()
    {
        // Güvenilir hâle gelmesi için mevcut verinin taranması ŞART: WITH CHECK CHECK.
        var script = Script(Table(checks: [Check()]), Table(checks: [Check(disabled: true, notTrusted: true)]));

        Assert.Contains("ALTER TABLE [dbo].[Customer] WITH CHECK CHECK CONSTRAINT [CK_Positive];", script);
    }

    [Fact]
    public void Enabling_an_untrusted_check_does_not_validate()
    {
        // Kaynakta da güvenilmezse doğrulama yapılmamalı; sadece aktifleştirilir.
        var script = Script(Table(checks: [Check(notTrusted: true)]), Table(checks: [Check(disabled: true, notTrusted: true)]));

        Assert.Contains("ALTER TABLE [dbo].[Customer] CHECK CONSTRAINT [CK_Positive];", script);
        Assert.DoesNotContain("WITH CHECK CHECK", script);
    }

    [Fact]
    public void Trust_only_change_is_scripted()
    {
        // Aktif ama güvenilmez → aktif ve güvenilir. Tek fark budur.
        var script = Script(Table(checks: [Check()]), Table(checks: [Check(notTrusted: true)]));

        Assert.Contains("WITH CHECK CHECK CONSTRAINT [CK_Positive];", script);
    }

    [Fact]
    public void Identical_state_produces_no_statement()
    {
        var script = Script(Table(checks: [Check(disabled: true)]), Table(checks: [Check(disabled: true)]));

        Assert.DoesNotContain("CONSTRAINT [CK_Positive]", script);
    }

    // --- yeni eklenen constraint ---

    [Fact]
    public void Newly_added_disabled_check_is_added_then_disabled()
    {
        var script = Script(Table(checks: [Check(disabled: true)]), Table());

        var add = script.IndexOf("ADD CONSTRAINT [CK_Positive]", StringComparison.Ordinal);
        var disable = script.IndexOf("NOCHECK CONSTRAINT [CK_Positive];", StringComparison.Ordinal);

        Assert.True(add >= 0 && disable > add, "önce eklenmeli, sonra kapatılmalı");
    }

    [Fact]
    public void Newly_added_disabled_check_is_not_validated_on_add()
    {
        // Pasif bir constraint'i WITH CHECK ile eklemek deploy'u patlatır: mevcut veri
        // onu ihlal ediyor olabilir, zaten bu yüzden kapatılmıştır.
        var script = Script(Table(checks: [Check(disabled: true)]), Table());

        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [CK_Positive]", script);
    }

    [Fact]
    public void Newly_added_untrusted_check_is_added_with_nocheck()
    {
        var script = Script(Table(checks: [Check(notTrusted: true)]), Table());

        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [CK_Positive]", script);
    }

    [Fact]
    public void Newly_added_trusted_check_is_validated()
    {
        var script = Script(Table(checks: [Check()]), Table());

        Assert.Contains("WITH CHECK ADD CONSTRAINT [CK_Positive]", script);
    }

    [Fact]
    public void Validate_option_off_still_adds_with_nocheck()
    {
        var script = Script(Table(checks: [Check()]), Table(),
            new TableScriptOptions { ValidateNewConstraints = false });

        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [CK_Positive]", script);
    }

    // --- foreign key ---

    [Fact]
    public void Disabling_an_existing_foreign_key_uses_nocheck_constraint()
    {
        var script = Script(Table(foreignKeys: [ForeignKey(disabled: true)]), Table(foreignKeys: [ForeignKey()]));

        Assert.Contains("ALTER TABLE [dbo].[Order] NOCHECK CONSTRAINT [FK_Order_Customer];", script);
        Assert.DoesNotContain("DROP CONSTRAINT", script);
    }

    [Fact]
    public void Newly_added_disabled_foreign_key_is_added_then_disabled()
    {
        var script = Script(Table(foreignKeys: [ForeignKey(disabled: true)]), Table());

        var add = script.IndexOf("ADD CONSTRAINT [FK_Order_Customer]", StringComparison.Ordinal);
        var disable = script.IndexOf("NOCHECK CONSTRAINT [FK_Order_Customer];", StringComparison.Ordinal);

        Assert.True(add >= 0 && disable > add, "önce eklenmeli, sonra kapatılmalı");
    }

    [Fact]
    public void Untrusted_foreign_key_is_added_without_validation()
    {
        var script = Script(Table(foreignKeys: [ForeignKey(notTrusted: true)]), Table());

        Assert.Contains("WITH NOCHECK ADD CONSTRAINT [FK_Order_Customer]", script);
    }

    // --- yeni tablo ---

    [Fact]
    public void New_table_disables_its_disabled_constraints_after_create()
    {
        // CREATE TABLE içindeki constraint hep AKTİF doğar; kapatma ancak sonra yazılabilir.
        var snapshot = Build(Table(checks: [Check(disabled: true)])).Objects[TestFactory.Table("Customer")];

        var create = snapshot.DisplayScript!.IndexOf("CREATE TABLE", StringComparison.Ordinal);
        var disable = snapshot.DisplayScript!.IndexOf("NOCHECK CONSTRAINT [CK_Positive];", StringComparison.Ordinal);

        Assert.True(create >= 0 && disable > create, "kapatma CREATE TABLE'dan sonra gelmeli");
    }

    [Fact]
    public void New_table_with_enabled_constraints_needs_no_state_statement()
    {
        var snapshot = Build(Table(checks: [Check()])).Objects[TestFactory.Table("Customer")];

        Assert.DoesNotContain("NOCHECK CONSTRAINT", snapshot.DisplayScript!);
    }
}
