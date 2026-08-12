using SchemaDiff.Core.Analysis;

namespace SchemaDiff.Tests;

// CoverageProbe canlı DB ister; burada sonda listesinin YAPISAL sağlığını doğruluyoruz:
// çift kayıt yok, her sorgu geçerli görünüyor, kritik boşluk sınıfları listede.
public class CoverageProbeTests
{
    [Fact]
    public void No_duplicate_probe_items()
    {
        var names = CoverageProbe.Probes.Select(p => $"{p.Category}/{p.Item}").ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_probe_has_a_count_select()
    {
        foreach (var (category, item, _, sql) in CoverageProbe.Probes)
        {
            Assert.False(string.IsNullOrWhiteSpace(category), "kategori boş olamaz");
            Assert.False(string.IsNullOrWhiteSpace(item), "başlık boş olamaz");
            Assert.StartsWith("SELECT", sql.TrimStart(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("COUNT(*)", sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData("Row-Level Security (security policy)")]
    [InlineData("Service Broker (queue/service/contract)")]
    [InlineData("Application role'ler")]
    [InlineData("Dynamic Data Masking kolonları")]
    [InlineData("Column Master/Encryption Key (Always Encrypted)")]
    [InlineData("External data source/table (PolyBase)")]
    [InlineData("Sıkıştırılmış partition'lar (DATA_COMPRESSION)")]
    [InlineData("XML schema collection'lar")]
    public void Known_gap_classes_are_probed(string item)
    {
        Assert.Contains(CoverageProbe.Probes, p => p.Item == item);
    }

    [Fact]
    public void Probe_count_is_substantial()
    {
        // Genişletme sonrası sonda listesi belirgin şekilde büyümeli.
        Assert.True(CoverageProbe.Probes.Length >= 35, $"beklenenden az sonda: {CoverageProbe.Probes.Length}");
    }
}
