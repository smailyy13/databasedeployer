using SchemaDiff.Core.Normalization;

namespace SchemaDiff.Tests;

public class TSqlNormalizerTests
{
    [Fact]
    public void Collapses_whitespace_between_tokens()
    {
        var a = TSqlNormalizer.Normalize("SELECT   a,\n\t b   FROM  t");
        var b = TSqlNormalizer.Normalize("SELECT a, b FROM t");
        Assert.Equal(b, a);
    }

    [Fact]
    public void Strips_line_and_block_comments_by_default()
    {
        var withComments = TSqlNormalizer.Normalize(
            "SELECT a -- satır yorumu\nFROM t /* blok yorumu */ WHERE a = 1");
        var without = TSqlNormalizer.Normalize("SELECT a FROM t WHERE a = 1");
        Assert.Equal(without, withComments);
    }

    [Fact]
    public void Does_not_treat_dash_dash_inside_string_literal_as_comment()
    {
        // Regex tabanlı bir normalize burada gövdeyi bozar; token akışı bozmaz.
        var sql = "SELECT '-- bu yorum değil' AS note";
        var normalized = TSqlNormalizer.Normalize(sql);
        Assert.Contains("-- bu yorum değil", normalized);
    }

    [Fact]
    public void Preserves_string_literal_casing_but_not_keyword_casing()
    {
        var options = new NormalizationOptions(IgnoreKeywordCasing: true);
        var a = TSqlNormalizer.Normalize("select 'Ali' from t", options: options);
        var b = TSqlNormalizer.Normalize("SELECT 'Ali' FROM t", options: options);
        Assert.Equal(b, a);
        Assert.Contains("'Ali'", a);
    }

    [Fact]
    public void Keyword_casing_difference_is_significant_when_not_ignored()
    {
        // Varsayılan: keyword casing korunur, yani 'select' ile 'SELECT' farklı metindir.
        var a = TSqlNormalizer.Normalize("select a from t");
        var b = TSqlNormalizer.Normalize("SELECT a FROM t");
        Assert.NotEqual(b, a);
    }

    [Fact]
    public void Distinct_identifier_casing_is_never_collapsed()
    {
        var options = new NormalizationOptions(IgnoreKeywordCasing: true);
        var a = TSqlNormalizer.Normalize("SELECT ColumnName FROM t", options: options);
        var b = TSqlNormalizer.Normalize("SELECT columnname FROM t", options: options);
        Assert.NotEqual(b, a);
    }

    [Fact]
    public void Bracketed_and_unbracketed_identifiers_are_equal()
    {
        var a = TSqlNormalizer.Normalize("SELECT [a], [b] FROM [dbo].[t]");
        var b = TSqlNormalizer.Normalize("SELECT a, b FROM dbo.t");
        Assert.Equal(b, a);
    }

    [Fact]
    public void Identifier_casing_still_distinguished_after_bracket_normalization()
    {
        // Parantez birleştirilir ama harf büyüklüğü asla — case-sensitive collation olabilir.
        var a = TSqlNormalizer.Normalize("SELECT [Name] FROM t");
        var b = TSqlNormalizer.Normalize("SELECT name FROM t");
        Assert.NotEqual(b, a);
    }

    [Fact]
    public void Identifier_quoting_normalization_can_be_disabled()
    {
        var options = new NormalizationOptions(NormalizeIdentifierQuoting: false);
        var a = TSqlNormalizer.Normalize("SELECT [a] FROM t", options: options);
        var b = TSqlNormalizer.Normalize("SELECT a FROM t", options: options);
        Assert.NotEqual(b, a);
    }

    [Fact]
    public void Null_or_whitespace_returns_empty()
    {
        Assert.Equal(string.Empty, TSqlNormalizer.Normalize(null));
        Assert.Equal(string.Empty, TSqlNormalizer.Normalize("   \n\t "));
    }

    [Fact]
    public void Unparseable_text_falls_back_without_throwing()
    {
        var garbage = "))) not sql at all ''' /* unterminated";
        var result = TSqlNormalizer.Normalize(garbage);
        Assert.False(string.IsNullOrEmpty(result));
    }

    [Fact]
    public void Comment_before_semicolon_does_not_leave_stray_space()
    {
        // Yorum atıldıktan sonra ';' önünde kalan ayırıcı boşluk eşitliği bozmamalı.
        var withComment = TSqlNormalizer.Normalize("SELECT 1 AS X /* not */;");
        var without = TSqlNormalizer.Normalize("SELECT 1 AS X;");
        Assert.Equal(without, withComment);
    }

    [Fact]
    public void Whitespace_before_semicolon_is_ignored()
    {
        Assert.Equal(TSqlNormalizer.Normalize("SELECT 1;"), TSqlNormalizer.Normalize("SELECT 1 ;"));
    }

    [Fact]
    public void Comment_before_comma_does_not_leave_stray_space()
    {
        var withComment = TSqlNormalizer.Normalize("SELECT a /* c */, b FROM t");
        var without = TSqlNormalizer.Normalize("SELECT a, b FROM t");
        Assert.Equal(without, withComment);
    }
}
