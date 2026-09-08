using SchemaDiff.Core.Normalization;

namespace SchemaDiff.Tests;

/// <summary>
/// "Boşlukları / yorumları / noktalı virgülü yok say" seçeneklerinin TÜM olası
/// durumlarını tek tek sınar. Her satır bir vaka: iki gövde YALNIZCA o boyutta farklı.
/// Kural: seçenek AÇIKken iki gövde AYNI kanoniğe iner (eşit); KAPALIyken farklı kalır.
/// </summary>
public class NormalizerOptionMatrixTests
{
    private static string N(string sql, NormalizationOptions o) => TSqlNormalizer.Normalize(sql, true, o);

    private static readonly NormalizationOptions WsOn  = NormalizationOptions.Default;                              // IgnoreWhitespace=true
    private static readonly NormalizationOptions WsOff = NormalizationOptions.Default with { IgnoreWhitespace = false };
    private static readonly NormalizationOptions CmtOn  = NormalizationOptions.Default;                             // IgnoreComments=true
    private static readonly NormalizationOptions CmtOff = NormalizationOptions.Default with { IgnoreComments = false };
    private static readonly NormalizationOptions SemiOn  = NormalizationOptions.Default with { IgnoreSemicolons = true };
    private static readonly NormalizationOptions SemiOff = NormalizationOptions.Default;                            // IgnoreSemicolons=false

    // ================= BOŞLUK =================

    public static readonly TheoryData<string, string> WhitespacePairs = new()
    {
        { "SELECT 1",                 "SELECT      1" },               // birden çok boşluk
        { "SELECT 1",                 "SELECT\t1" },                   // sekme
        { "SELECT 1",                 "SELECT\n1" },                   // satır sonu
        { "SELECT 1",                 "SELECT\r\n\t 1" },              // karışık
        { "SELECT a, b FROM t",       "SELECT a,b FROM t" },           // virgülden SONRA boşluk
        { "SELECT a, b FROM t",       "SELECT a , b FROM t" },         // virgülden ÖNCE+sonra
        { "SELECT a,b,c",             "SELECT a , b , c" },            // çok virgül
        { "SELECT (a) FROM t",        "SELECT ( a) FROM t" },          // '(' sonrası
        { "SELECT (a) FROM t",        "SELECT (a ) FROM t" },          // ')' öncesi
        { "SELECT (a) FROM t",        "SELECT ( a ) FROM t" },         // parantez içi iki yan
        { "WHERE a=1",                "WHERE a = 1" },                 // '=' çevresi
        { "WHERE a>=1",               "WHERE a >= 1" },                // '>=' çevresi
        { "WHERE a<b",                "WHERE a < b" },                 // '<' çevresi
        { "SELECT a+b",               "SELECT a + b" },                // '+' çevresi
        { "EXEC(@s)",                 "EXEC (@s)" },                   // çağrı parantezi
        { "dbo.T",                    "dbo . T" },                     // nitelik noktası
        { "SELECT 1;",                "SELECT 1 ;" },                  // ';' öncesi (';' korunur, boşluk değil)
    };

    [Theory]
    [MemberData(nameof(WhitespacePairs))]
    public void Whitespace_ignored_makes_pair_equal(string a, string b) =>
        Assert.Equal(N(a, WsOn), N(b, WsOn));

    [Theory]
    [MemberData(nameof(WhitespacePairs))]
    public void Whitespace_significant_keeps_pair_different(string a, string b) =>
        Assert.NotEqual(N(a, WsOff), N(b, WsOff));

    [Fact]
    public void Leading_and_trailing_whitespace_is_always_trimmed()
    {
        // Gövdenin baş/son boşluğu hiçbir zaman anlamlı değildir (boşluk yok sayılsa da sayılmasa da).
        Assert.Equal(N("SELECT 1 AS X", WsOn),  N("  SELECT 1 AS X  ", WsOn));
        Assert.Equal(N("SELECT 1 AS X", WsOff), N("  SELECT 1 AS X  ", WsOff));
    }

    // ================= YORUM =================

    public static readonly TheoryData<string, string> CommentPairs = new()
    {
        { "SELECT 1 AS X",            "SELECT 1 AS X /* not */" },     // sonda blok yorum
        { "SELECT 1 AS X",            "SELECT 1 AS X -- not\n" },      // sonda satır yorumu
        { "SELECT 1 AS X",            "SELECT /* mid */ 1 AS X" },     // ortada blok yorum
        { "SELECT a, b FROM t",       "SELECT a /* c */, b FROM t" },  // virgülden önce yorum
        { "SELECT a FROM t",          "SELECT a FROM t -- son\n" },    // sonda satır yorumu (FROM'lu)
        { "SELECT 1",                 "/* baş */ SELECT 1" },          // başta yorum
        { "SELECT 1 AS X",            "SELECT 1 /* a */ /* b */ AS X" }, // iki yorum
        { "SELECT 1\nFROM t",         "SELECT 1 -- satır\nFROM t" },   // satır yorumu satır ortasında
    };

    [Theory]
    [MemberData(nameof(CommentPairs))]
    public void Comments_ignored_makes_pair_equal(string a, string b) =>
        Assert.Equal(N(a, CmtOn), N(b, CmtOn));

    [Theory]
    [MemberData(nameof(CommentPairs))]
    public void Comments_significant_keeps_pair_different(string a, string b) =>
        Assert.NotEqual(N(a, CmtOff), N(b, CmtOff));

    [Fact]
    public void Comment_marker_inside_string_literal_is_never_stripped()
    {
        // '--' ve '/*' string literal içinde yorum DEĞİLDİR — silinmemeli.
        Assert.Contains("-- değil", N("SELECT '-- değil' AS n", CmtOn));
        Assert.Contains("/* değil", N("SELECT '/* değil */' AS n", CmtOn));
    }

    // ================= NOKTALI VİRGÜL =================

    public static readonly TheoryData<string, string> SemicolonPairs = new()
    {
        { "SELECT 1",                            "SELECT 1;" },                       // sondaki ';'
        { "BEGIN SELECT 1 SELECT 2 END",         "BEGIN SELECT 1; SELECT 2; END" },   // ifadeler arası
        { "DECLARE @i INT SET @i = 1",           "DECLARE @i INT; SET @i = 1;" },     // declare/set
        { "SELECT 1",                            "SELECT 1;;" },                      // çift ';'
        { "SELECT 1 SELECT 2",                   "SELECT 1 ; SELECT 2 ;" },           // boşluklu ';'
        { "SET NOCOUNT ON SELECT 1",             "SET NOCOUNT ON; SELECT 1" },        // set + select
    };

    [Theory]
    [MemberData(nameof(SemicolonPairs))]
    public void Semicolons_ignored_makes_pair_equal(string a, string b) =>
        Assert.Equal(N(a, SemiOn), N(b, SemiOn));

    [Theory]
    [MemberData(nameof(SemicolonPairs))]
    public void Semicolons_significant_keeps_pair_different(string a, string b) =>
        Assert.NotEqual(N(a, SemiOff), N(b, SemiOff));
}
