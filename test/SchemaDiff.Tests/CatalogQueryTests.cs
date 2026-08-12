using System.Reflection;
using SchemaDiff.Core.Analysis;
using SchemaDiff.Core.Extraction;

namespace SchemaDiff.Tests;

/// <summary>
/// Katalog sorgularının YAPISAL sağlığı. Bu sorgular ancak canlı bir SQL Server'a karşı
/// çalıştırılınca doğrulanır — burada sunucusuz doğrulanabilecek her şeyi doğruluyoruz.
///
/// En kritiği salt-okunurluk: araç bir bankada üretim veritabanına bağlanıyor ve
/// "yalnızca katalog okur" sözü veriyor. Bir sorguya yanlışlıkla yazan bir ifade
/// girerse bunu derleyici değil, yalnızca bu test yakalar.
/// </summary>
public class CatalogQueryTests
{
    /// <summary>Sql sınıfındaki tüm sorgu sabitleri (ad, metin).</summary>
    public static TheoryData<string, string> Queries
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var constant in typeof(Sql)
                         .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
                         .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string)))
            {
                data.Add(constant.Name, (string)constant.GetRawConstantValue()!);
            }
            return data;
        }
    }

    private static readonly string[] WritingKeywords =
    [
        "INSERT ", "UPDATE ", "DELETE ", "MERGE ", "DROP ", "ALTER ", "CREATE ",
        "TRUNCATE ", "EXEC ", "EXECUTE ", "GRANT ", "REVOKE ", "DENY ", "BACKUP ", "RESTORE ",
    ];

    [Fact]
    public void All_queries_are_discovered()
    {
        // Yansımayla topluyoruz; sıfır sorgu bulmak testin sessizce boşa dönmesi demektir.
        Assert.True(Queries.Count >= 30, $"beklenenden az sorgu bulundu: {Queries.Count}");
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_is_read_only(string name, string sql)
    {
        Assert.Null(WritingKeywordIn(sql, name));
    }

    [Theory]
    [InlineData("SELECT 1; DROP TABLE dbo.T;")]
    [InlineData("SELECT name FROM sys.objects; DELETE FROM sys.x;")]
    [InlineData("SELECT 1\nEXEC sp_who")]
    public void Read_only_check_rejects_a_writing_statement(string sql)
    {
        // Denetçinin kendisi çalışıyor mu: yakalayamayan bir denetim, denetim değildir.
        Assert.NotNull(WritingKeywordIn(sql, "sahte"));
    }

    /// <summary>Sorguda yazan bir ifade varsa hata mesajını, yoksa null döner.</summary>
    private static string? WritingKeywordIn(string sql, string name)
    {
        var upper = " " + sql.ToUpperInvariant().Replace('\n', ' ').Replace('\r', ' ');

        foreach (var keyword in WritingKeywords)
            if (upper.Contains(" " + keyword, StringComparison.Ordinal))
                return $"{name}: salt-okunur olmalı ama '{keyword.Trim()}' geçiyor";

        return null;
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_selects_and_terminates(string name, string sql)
    {
        var trimmed = sql.Trim();
        Assert.True(trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase), $"{name}: SELECT ile başlamalı");
        Assert.True(trimmed.EndsWith(';'), $"{name}: noktalı virgülle bitmeli");
        // SELECT * kolon sırasına bağımlıdır; mapper'lar ordinalle okuduğu için yasak.
        Assert.False(trimmed.Contains("SELECT *", StringComparison.OrdinalIgnoreCase), $"{name}: SELECT * kullanamaz");
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_parentheses_are_balanced(string name, string sql)
    {
        var depth = 0;
        foreach (var c in sql)
        {
            if (c == '(') depth++;
            else if (c == ')') depth--;
            Assert.True(depth >= 0, $"{name}: fazladan kapanış parantezi");
        }
        Assert.Equal(0, depth);
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_reads_only_system_catalog(string name, string sql)
    {
        // FROM/JOIN hedefleri yalnızca sys.* olmalı: kullanıcı tablosuna dokunulmaz.
        foreach (var keyword in new[] { "FROM ", "JOIN " })
        {
            var index = 0;
            while ((index = sql.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var rest = sql[(index + keyword.Length)..].TrimStart();
                index += keyword.Length;
                if (rest.StartsWith('(')) continue; // alt sorgu — kendi FROM'u ayrıca denetlenir

                Assert.True(rest.StartsWith("sys.", StringComparison.OrdinalIgnoreCase),
                    $"{name}: yalnızca sys.* okunmalı, '{rest.Split(' ')[0]}' okunuyor");
            }
        }
    }

    [Fact]
    public void Statistics_query_excludes_auto_created_and_index_statistics()
    {
        // Otomatik/index istatistikleri şema farkı değildir; filtre kaybolursa her
        // karşılaştırma yüzlerce sahte farkla dolar.
        Assert.Contains("user_created = 1", Sql.Statistics, StringComparison.Ordinal);
        Assert.Contains("user_created = 1", Sql.StatisticColumns, StringComparison.Ordinal);
    }

    [Fact]
    public void Statistic_columns_query_carries_the_ordinal()
    {
        // stats_column_id istatistikteki kolon sırasıdır — ilk kolon histogramı taşır.
        Assert.Contains("stats_column_id", Sql.StatisticColumns, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Queries))]
    public void Query_skips_microsoft_shipped_objects_when_it_reads_objects(string name, string sql)
    {
        // sys.objects'e katılan her sorgu sistem objelerini dışarıda bırakmalı; aksi hâlde
        // rapor MS objeleriyle dolar. (sys.objects'i kullanmayan sorgular bu testi atlar.)
        if (!sql.Contains("sys.objects", StringComparison.OrdinalIgnoreCase)) return;
        if (name is nameof(Sql.Objects)) return; // kendi WHERE'inde zaten var, ayrıca test ediliyor

        Assert.Contains("is_ms_shipped = 0", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Objects_query_excludes_system_and_history_tables()
    {
        Assert.Contains("is_ms_shipped = 0", Sql.Objects, StringComparison.Ordinal);
        Assert.Contains("temporal_type = 1", Sql.Objects, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CoverageProbeQueries))]
    public void Coverage_probe_query_is_read_only(string item, string sql)
    {
        Assert.Null(WritingKeywordIn(sql, item));
    }

    public static TheoryData<string, string> CoverageProbeQueries
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var probe in CoverageProbe.Probes) data.Add(probe.Item, probe.Sql);
            return data;
        }
    }
}
