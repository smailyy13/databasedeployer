using System.Globalization;
using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>
/// XML ve spatial index metni. Genel <c>CREATE INDEX</c> yazımı bunlarda GEÇERSİZ SQL üretir
/// (XML'de ASC/DESC yok ve PRIMARY/USING zorunlu; spatial'da tessellation şart), bu yüzden
/// ayrı tutuluyorlar. Tek yerde: yeni tablonun CREATE'i ile ALTER yolu aynı metni üretmeli.
/// </summary>
internal static class SpecialIndexScript
{
    public static string Create(string qualified, XmlIndexDefinition idx) => idx.IsPrimary
        ? $"CREATE PRIMARY XML INDEX [{idx.Name}] ON {qualified} ([{idx.Column}]);"
        : $"CREATE XML INDEX [{idx.Name}] ON {qualified} ([{idx.Column}]) " +
          $"USING XML INDEX [{idx.PrimaryIndexName}] FOR {idx.SecondaryType};";

    public static string Create(string qualified, SpatialIndexDefinition idx)
    {
        var sb = new StringBuilder($"CREATE SPATIAL INDEX [{idx.Name}] ON {qualified} ([{idx.Column}])")
            .Append(" USING ").Append(idx.Tessellation);

        var options = new List<string>(3);
        if (idx.HasBoundingBox)
        {
            options.Add(
                $"BOUNDING_BOX = ({Number(idx.BoundingXMin)}, {Number(idx.BoundingYMin)}, " +
                $"{Number(idx.BoundingXMax)}, {Number(idx.BoundingYMax)})");
        }

        // AUTO_GRID'de seviyeler SQL Server tarafından seçilir; yazmak hatadır.
        if (!idx.IsAutoGrid && idx.Level1 is not null)
        {
            options.Add(
                $"GRIDS = (LEVEL_1 = {idx.Level1}, LEVEL_2 = {idx.Level2}, " +
                $"LEVEL_3 = {idx.Level3}, LEVEL_4 = {idx.Level4})");
        }

        if (idx.CellsPerObject is { } cells)
            options.Add($"CELLS_PER_OBJECT = {cells.ToString(CultureInfo.InvariantCulture)}");

        if (options.Count > 0) sb.Append(" WITH (").Append(string.Join(", ", options)).Append(')');
        sb.Append(';');
        return sb.ToString();
    }

    public static string Drop(string qualified, string name) => $"DROP INDEX [{name}] ON {qualified};";

    /// <summary>Koordinatlar deterministik yazılmalı: kültüre bağlı ondalık ayıracı SQL'i bozar.</summary>
    private static string Number(double? value) =>
        (value ?? 0).ToString("0.############################", CultureInfo.InvariantCulture);
}
