using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

public class ObjectKeyTests
{
    [Fact]
    public void CaseInsensitive_comparer_treats_differing_case_as_equal()
    {
        var a = new ObjectKey("dbo", "Customer", ObjectKind.Table);
        var b = new ObjectKey("DBO", "CUSTOMER", ObjectKind.Table);

        Assert.True(ObjectKeyComparer.CaseInsensitive.Equals(a, b));
        Assert.Equal(
            ObjectKeyComparer.CaseInsensitive.GetHashCode(a),
            ObjectKeyComparer.CaseInsensitive.GetHashCode(b));
    }

    [Fact]
    public void CaseSensitive_comparer_distinguishes_case()
    {
        var a = new ObjectKey("dbo", "Customer", ObjectKind.Table);
        var b = new ObjectKey("dbo", "customer", ObjectKind.Table);

        Assert.False(ObjectKeyComparer.CaseSensitive.Equals(a, b));
    }

    [Fact]
    public void Different_kind_same_name_are_not_equal()
    {
        var table = new ObjectKey("dbo", "X", ObjectKind.Table);
        var view = new ObjectKey("dbo", "X", ObjectKind.View);

        Assert.False(ObjectKeyComparer.CaseInsensitive.Equals(table, view));
    }

    [Theory]
    [InlineData("U", ObjectKind.Table)]
    [InlineData("V", ObjectKind.View)]
    [InlineData("P", ObjectKind.Procedure)]
    [InlineData("FN", ObjectKind.ScalarFunction)]
    [InlineData("IF", ObjectKind.InlineTableFunction)]
    [InlineData("TF", ObjectKind.TableFunction)]
    [InlineData("TR", ObjectKind.Trigger)]
    [InlineData("SN", ObjectKind.Synonym)]
    [InlineData("SO", ObjectKind.Sequence)]
    [InlineData("XX", ObjectKind.Unknown)]
    public void FromSysType_maps_catalog_codes(string code, ObjectKind expected)
    {
        Assert.Equal(expected, ObjectKindMap.FromSysType(code));
    }

    [Theory]
    [InlineData(ObjectKind.View, true)]
    [InlineData(ObjectKind.Procedure, true)]
    [InlineData(ObjectKind.Trigger, true)]
    [InlineData(ObjectKind.Table, false)]
    [InlineData(ObjectKind.Sequence, false)]
    [InlineData(ObjectKind.Schema, false)]
    public void IsModule_identifies_body_carrying_kinds(ObjectKind kind, bool expected)
    {
        Assert.Equal(expected, kind.IsModule());
    }

    [Fact]
    public void ToString_formats_schema_kind_specially()
    {
        Assert.Equal("SCHEMA [sales]", new ObjectKey("", "sales", ObjectKind.Schema).ToString());
        Assert.Equal("Table [dbo].[Customer]", new ObjectKey("dbo", "Customer", ObjectKind.Table).ToString());
    }
}
