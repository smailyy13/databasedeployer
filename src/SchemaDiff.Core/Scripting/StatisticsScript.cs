using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>
/// CREATE / DROP STATISTICS metni. Tek yerde tutulur: yeni tablonun CREATE script'i
/// (<see cref="TableScriptWriter"/>) ile değişen tablonun ALTER script'i
/// (<see cref="TableScriptGenerator"/>) aynı metni üretsin — aksi hâlde ekranda görünen
/// script ile dağıtılan script birbirinden ayrışır.
/// </summary>
internal static class StatisticsScript
{
    /// <param name="qualified">Tablonun tam adı: <c>[şema].[ad]</c>.</param>
    public static string Create(string qualified, StatisticsDefinition stat)
    {
        var sb = new StringBuilder(128);
        sb.Append("CREATE STATISTICS [").Append(stat.Name).Append("] ON ").Append(qualified)
          .Append(" (").Append(string.Join(", ", stat.Columns.Select(c => $"[{c}]"))).Append(')');

        if (stat.FilterDefinition is not null) sb.Append(" WHERE ").Append(stat.FilterDefinition);

        // Örnekleme oranı katalogda tutulmaz; yalnızca kalıcı ayarlar yazılır.
        var with = new List<string>(2);
        if (stat.NoRecompute) with.Add("NORECOMPUTE");
        if (stat.IsIncremental) with.Add("INCREMENTAL = ON");
        if (with.Count > 0) sb.Append(" WITH ").Append(string.Join(", ", with));

        sb.Append(';');
        return sb.ToString();
    }

    /// <summary>DROP STATISTICS tabloyu ada gömer: <c>DROP STATISTICS [şema].[tablo].[istatistik];</c></summary>
    public static string Drop(string qualified, string name) =>
        $"DROP STATISTICS {qualified}.[{name}];";
}
