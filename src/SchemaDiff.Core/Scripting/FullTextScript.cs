using System.Globalization;
using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>
/// CREATE / DROP FULLTEXT INDEX metni. İstatistiklerde olduğu gibi tek yerde tutulur:
/// yeni tablonun CREATE script'i ile değişen tablonun ALTER script'i aynı metni üretmeli.
/// </summary>
internal static class FullTextScript
{
    /// <param name="qualified">Tablonun tam adı: <c>[şema].[ad]</c>.</param>
    public static string Create(string qualified, FullTextIndexDefinition ft)
    {
        var columns = ft.Columns.Select(c =>
        {
            var sb = new StringBuilder($"[{c.Column}]");
            if (c.TypeColumn is not null) sb.Append(" TYPE COLUMN [").Append(c.TypeColumn).Append(']');
            // LCID 0 "sunucu varsayılanı" demektir; yazmak ortama bağımlılık yaratır.
            if (c.LanguageId != 0)
                sb.Append(" LANGUAGE ").Append(c.LanguageId.ToString(CultureInfo.InvariantCulture));
            return sb.ToString();
        });

        var script = new StringBuilder("CREATE FULLTEXT INDEX ON ").Append(qualified)
            .Append(" (").Append(string.Join(", ", columns)).Append(')')
            .Append(" KEY INDEX [").Append(ft.KeyIndexName).Append(']')
            .Append(" ON [").Append(ft.CatalogName).Append(']')
            .Append(" WITH (CHANGE_TRACKING = ").Append(ft.ChangeTracking)
            .Append(", STOPLIST = ").Append(Stoplist(ft.Stoplist)).Append(");");

        return script.ToString();
    }

    /// <summary>Pasif index: CREATE her zaman AKTİF doğar, kapatma ayrı ifadedir.</summary>
    public static string Disable(string qualified) =>
        $"ALTER FULLTEXT INDEX ON {qualified} DISABLE;";

    public static string Enable(string qualified) =>
        $"ALTER FULLTEXT INDEX ON {qualified} ENABLE;";

    public static string Drop(string qualified) =>
        $"DROP FULLTEXT INDEX ON {qualified};";

    /// <summary>OFF ve SYSTEM anahtar kelimedir; kullanıcı stoplist'i köşeli parantezle yazılır.</summary>
    private static string Stoplist(string stoplist) =>
        stoplist is "OFF" or "SYSTEM" ? stoplist : $"[{stoplist}]";
}
