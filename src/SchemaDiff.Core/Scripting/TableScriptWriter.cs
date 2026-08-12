using System.Globalization;
using System.Text;
using SchemaDiff.Core.Extraction;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

/// <summary>Tablo script'i üretmek için gereken katalog aramaları.</summary>
internal sealed class TableScriptSources
{
    public required Dictionary<int, List<ColumnRow>> ColumnsBy { get; init; }
    public required Dictionary<int, List<IndexRow>> IndexesBy { get; init; }
    public required Dictionary<long, List<IndexColumnRow>> IndexColumnsBy { get; init; }
    public required Dictionary<long, KeyConstraintRow> KeyConstraintByIndex { get; init; }
    public required Dictionary<int, List<CheckConstraintRow>> ChecksBy { get; init; }
    public required Dictionary<int, List<ForeignKeyRow>> ForeignKeysBy { get; init; }
    public required Dictionary<int, List<ForeignKeyColumnRow>> FkColumnsBy { get; init; }
    public required Dictionary<long, string> ColumnNames { get; init; }
    public Dictionary<int, string> XmlCollections { get; init; } = [];
    public required Dictionary<int, ObjectKey> KeyById { get; init; }
    public Dictionary<int, TemporalRow> TemporalBy { get; init; } = [];
    public Dictionary<int, List<StatisticRow>> StatisticsBy { get; init; } = [];
    public Dictionary<long, List<StatisticColumnRow>> StatisticColumnsBy { get; init; } = [];
    public Dictionary<int, FullTextIndexDefinition> FullTextBy { get; init; } = [];
    public Dictionary<long, IndexExtraRow> IndexExtrasBy { get; init; } = [];
    public Dictionary<int, List<XmlIndexDefinition>> XmlIndexesBy { get; init; } = [];
    public Dictionary<int, List<SpatialIndexDefinition>> SpatialIndexesBy { get; init; } = [];
}

/// <summary>
/// Tablonun okunabilir CREATE script'ini üretir.
///
/// SQL Server tablonun özgün DDL metnini SAKLAMAZ; böyle bir "şema dosyası" yoktur.
/// SSMS'teki "Script Table as CREATE" de bu metni katalogdan üretir. Bu sınıf aynısını
/// yapar — amacı deployment değil, arayüzde değişikliği bağlamıyla birlikte
/// (öncesi ve sonrasıyla, gerçek satır numaralarıyla) gösterebilmek.
///
/// İki taraf da aynı üreticiden geçtiği için biçim farkı diff'e karışmaz. Karşılaştırmada
/// yok sayılan şeyler (sistem üretimi adlar, collation) burada da yazılmaz — aksi hâlde
/// ekranda "fark" görünüp listede görünmeyen satırlar olurdu.
/// </summary>
internal static class TableScriptWriter
{
    public static string Write(ObjectKey key, int objectId, TableScriptSources sources, SnapshotOptions options)
    {
        var columns = sources.ColumnsBy.GetValueOrDefault(objectId) ?? [];
        var sb = new StringBuilder(columns.Count * 64 + 256);

        sb.Append("CREATE TABLE ").Append(Quote(key.Schema, key.Name)).AppendLine(" (");

        var body = new List<string>(columns.Count + 8);
        var nameWidth = columns.Count == 0 ? 0 : columns.Max(c => c.Name.Length + 2);
        var typeWidth = columns.Count == 0 ? 0 : columns.Max(c => FormatType(c, sources).Length);

        foreach (var column in columns.OrderBy(c => c.ColumnId))
            body.Add(WriteColumn(column, nameWidth, typeWidth, sources, options));

        body.AddRange(WriteTableConstraints(objectId, sources, options));

        // System-versioned temporal tablo: PERIOD satırı body'nin sonuna, SYSTEM_VERSIONING
        // ise ")" son ekine gelir. Generated-always kolonlar ancak böyle geçerli olur.
        var temporal = sources.TemporalBy.GetValueOrDefault(objectId);
        if (temporal is { StartColumn: { } start, EndColumn: { } end })
            body.Add($"    PERIOD FOR SYSTEM_TIME ([{start}], [{end}])");

        for (var i = 0; i < body.Count; i++)
            sb.Append(body[i]).AppendLine(i < body.Count - 1 ? "," : string.Empty);

        sb.Append(')').Append(SystemVersioningSuffix(temporal)).AppendLine(";");

        foreach (var line in WriteIndexes(key, objectId, sources, options))
        {
            sb.AppendLine();
            sb.Append(line);
        }

        foreach (var line in WriteStatistics(key, objectId, sources, options))
        {
            sb.AppendLine();
            sb.Append(line);
        }

        foreach (var line in WriteSpecialIndexes(key, objectId, sources))
        {
            sb.AppendLine();
            sb.Append(line);
        }

        foreach (var line in WriteFullText(key, objectId, sources))
        {
            sb.AppendLine();
            sb.Append(line);
        }

        foreach (var line in WriteConstraintState(key, objectId, sources))
        {
            sb.AppendLine();
            sb.Append(line);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Temporal tablo için <c>) WITH (SYSTEM_VERSIONING = ON [(HISTORY_TABLE = [s].[n])])</c>.
    /// Otomatik adlı history'de ad ortama göre değiştiği için HISTORY_TABLE yazılmaz —
    /// SQL Server o durumda history tablosunu kendisi oluşturur.
    /// </summary>
    private static string SystemVersioningSuffix(TemporalRow? temporal)
    {
        if (temporal is null || temporal.StartColumn is null) return string.Empty;
        var auto = temporal.HistoryName is null
            || temporal.HistoryName.StartsWith("MSSQL_TemporalHistoryFor_", StringComparison.OrdinalIgnoreCase);
        var history = auto
            ? string.Empty
            : $" (HISTORY_TABLE = [{temporal.HistorySchema}].[{temporal.HistoryName}])";
        return $" WITH (SYSTEM_VERSIONING = ON{history})";
    }

    // --- kolonlar ---

    private static string WriteColumn(
        ColumnRow column, int nameWidth, int typeWidth, TableScriptSources sources, SnapshotOptions options)
    {
        var name = $"[{column.Name}]".PadRight(nameWidth);

        if (column.IsComputed)
        {
            var persisted = column.ComputedIsPersisted == true ? " PERSISTED" : string.Empty;
            return $"    {name} AS {column.ComputedDefinition}{persisted}";
        }

        var sb = new StringBuilder("    ");
        sb.Append(name).Append(' ').Append(FormatType(column, sources).PadRight(typeWidth));

        // Sıra T-SQL grameriyle aynı: FILESTREAM → COLLATE → SPARSE → COLUMN_SET → ROWGUIDCOL.
        if (column.IsFileStream) sb.Append(" FILESTREAM");

        if (column.Collation is not null && !options.IgnoreCollation)
            sb.Append(" COLLATE ").Append(column.Collation);

        if (column.IsSparse) sb.Append(" SPARSE");
        if (column.IsColumnSet) sb.Append(" COLUMN_SET FOR ALL_SPARSE_COLUMNS");
        if (column.IsRowGuidCol) sb.Append(" ROWGUIDCOL");

        if (column.IsIdentity)
        {
            sb.Append(" IDENTITY");
            if (!options.IgnoreIdentitySeed)
                sb.Append(" (").Append(column.IdentitySeed).Append(", ").Append(column.IdentityIncrement).Append(')');
            if (column.IdentityNotForReplication) sb.Append(" NOT FOR REPLICATION");
        }

        // Temporal PERIOD kolonu: GENERATED ALWAYS AS ROW START/END [HIDDEN].
        // (Geçerli olması için CREATE TABLE'da PERIOD FOR SYSTEM_TIME de bulunmalıdır;
        //  bu yüzden yalnızca tablo temporal olarak yazılıyorsa üretilir.)
        if (column.GeneratedAlwaysType == 1) sb.Append(" GENERATED ALWAYS AS ROW START");
        else if (column.GeneratedAlwaysType == 2) sb.Append(" GENERATED ALWAYS AS ROW END");
        if (column.GeneratedAlwaysType != 0 && column.IsHidden) sb.Append(" HIDDEN");

        sb.Append(column.IsNullable ? " NULL" : " NOT NULL");

        if (column.DefaultDefinition is not null)
        {
            var named = !(options.IgnoreSystemNamedConstraints && column.DefaultIsSystemNamed == true);
            if (named && column.DefaultName is not null) sb.Append(" CONSTRAINT [").Append(column.DefaultName).Append(']');
            sb.Append(" DEFAULT ").Append(column.DefaultDefinition);
        }

        return sb.ToString();
    }

    private static string FormatType(ColumnRow column, TableScriptSources sources)
    {
        // Kullanıcı tanımlı tipler şema nitelemesiyle yazılır.
        if (!string.Equals(column.TypeSchema, "sys", StringComparison.OrdinalIgnoreCase))
            return Quote(column.TypeSchema, column.TypeName);

        var upper = column.TypeName.ToUpperInvariant();
        var length = column.MaxLength.ToString(CultureInfo.InvariantCulture);

        // Tipli XML: xml(CONTENT|DOCUMENT [şema].[koleksiyon]). Koleksiyon adı çözülemezse
        // düz XML yazmak yanlış kolon üretir — bu yüzden yalnız ad varsa ek yazılır.
        if (column.XmlCollectionId != 0
            && sources.XmlCollections.TryGetValue(column.XmlCollectionId, out var collection))
        {
            return $"{upper} ({(column.IsXmlDocument ? "DOCUMENT" : "CONTENT")} {collection})";
        }

        return column.TypeName.ToLowerInvariant() switch
        {
            "varchar" or "char" or "varbinary" or "binary" =>
                column.MaxLength == -1 ? $"{upper} (MAX)" : $"{upper} ({length})",
            "nvarchar" or "nchar" =>
                column.MaxLength == -1 ? $"{upper} (MAX)" : $"{upper} ({column.MaxLength / 2})",
            "decimal" or "numeric" => $"{upper} ({column.Precision}, {column.Scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{upper} ({column.Scale})",
            "float" => column.Precision == 53 ? upper : $"{upper} ({column.Precision})",
            _ => upper,
        };
    }

    // --- tablo içi kısıtlar ---

    private static IEnumerable<string> WriteTableConstraints(
        int objectId, TableScriptSources sources, SnapshotOptions options)
    {
        var lines = new List<string>();

        // PK ve UNIQUE, kendilerini taşıyan index üzerinden yazılır.
        foreach (var index in (sources.IndexesBy.GetValueOrDefault(objectId) ?? [])
                     .Where(i => i.IsPrimaryKey || i.IsUniqueConstraint)
                     .OrderByDescending(i => i.IsPrimaryKey))
        {
            var pairKey = Pair(objectId, index.IndexId);
            sources.KeyConstraintByIndex.TryGetValue(pairKey, out var constraint);

            var prefix = NameConstraint(constraint?.Name, constraint?.IsSystemNamed ?? true, options);
            var kind = index.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
            var clustered = index.TypeDesc.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase)
                            && !index.TypeDesc.StartsWith("NON", StringComparison.OrdinalIgnoreCase)
                ? "CLUSTERED" : "NONCLUSTERED";

            lines.Add($"    {prefix}{kind} {clustered} ({KeyColumns(objectId, index.IndexId, sources)})");
        }

        foreach (var check in (sources.ChecksBy.GetValueOrDefault(objectId) ?? []).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var prefix = NameConstraint(check.Name, check.IsSystemNamed, options);
            // Gramer: CHECK [NOT FOR REPLICATION] (ifade).
            var nfr = check.IsNotForReplication ? "NOT FOR REPLICATION " : string.Empty;
            lines.Add($"    {prefix}CHECK {nfr}{check.Definition}");
        }

        foreach (var foreignKey in (sources.ForeignKeysBy.GetValueOrDefault(objectId) ?? []).OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            var prefix = NameConstraint(foreignKey.Name, foreignKey.IsSystemNamed, options);
            var columns = sources.FkColumnsBy.GetValueOrDefault(foreignKey.ObjectId) ?? [];

            var parentColumns = string.Join(", ", columns.OrderBy(c => c.ConstraintColumnId)
                .Select(c => $"[{ColumnName(sources, c.ParentObjectId, c.ParentColumnId)}]"));
            var referencedColumns = string.Join(", ", columns.OrderBy(c => c.ConstraintColumnId)
                .Select(c => $"[{ColumnName(sources, c.ReferencedObjectId, c.ReferencedColumnId)}]"));

            var referenced = sources.KeyById.TryGetValue(foreignKey.ReferencedObjectId, out var refKey)
                ? Quote(refKey.Schema, refKey.Name)
                : $"[#{foreignKey.ReferencedObjectId}]";

            var actions = new StringBuilder();
            if (foreignKey.DeleteAction != 0) actions.Append(" ON DELETE ").Append(ReferentialAction(foreignKey.DeleteAction));
            if (foreignKey.UpdateAction != 0) actions.Append(" ON UPDATE ").Append(ReferentialAction(foreignKey.UpdateAction));
            if (foreignKey.IsNotForReplication) actions.Append(" NOT FOR REPLICATION");

            lines.Add($"    {prefix}FOREIGN KEY ({parentColumns}) REFERENCES {referenced} ({referencedColumns}){actions}");
        }

        return lines;
    }

    private static string NameConstraint(string? name, bool isSystemNamed, SnapshotOptions options) =>
        name is null || (options.IgnoreSystemNamedConstraints && isSystemNamed)
            ? string.Empty
            : $"CONSTRAINT [{name}] ";

    private static string ReferentialAction(byte action) => action switch
    {
        1 => "CASCADE",
        2 => "SET NULL",
        3 => "SET DEFAULT",
        _ => "NO ACTION",
    };

    // --- bağımsız index'ler ---

    private static IEnumerable<string> WriteIndexes(
        ObjectKey key, int objectId, TableScriptSources sources, SnapshotOptions options)
    {
        var lines = new List<string>();

        foreach (var index in (sources.IndexesBy.GetValueOrDefault(objectId) ?? [])
                     .Where(i => !i.IsPrimaryKey && !i.IsUniqueConstraint && i.Name is not null)
                     .OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            var sb = new StringBuilder();
            sb.Append("GO").AppendLine();
            sb.Append("CREATE ");
            if (index.IsUnique) sb.Append("UNIQUE ");
            sb.Append(index.TypeDesc).Append(" INDEX [").Append(index.Name).AppendLine("]");
            sb.Append("    ON ").Append(Quote(key.Schema, key.Name))
              .Append('(').Append(KeyColumns(objectId, index.IndexId, sources)).Append(')');

            var included = IncludedColumns(objectId, index.IndexId, sources);
            if (included.Length > 0) sb.AppendLine().Append("    INCLUDE(").Append(included).Append(')');

            if (index.FilterDefinition is not null)
                sb.AppendLine().Append("    WHERE ").Append(index.FilterDefinition);

            // Fiziksel seçenekler tek WITH listesinde toplanır; varsayılandan sapanlar yazılır.
            if (!options.IgnoreIndexPhysicalOptions)
            {
                var withOptions = new List<string>(5);
                if (index.FillFactor > 0)
                    withOptions.Add($"FILLFACTOR = {index.FillFactor.ToString(CultureInfo.InvariantCulture)}");
                if (!index.AllowRowLocks) withOptions.Add("ALLOW_ROW_LOCKS = OFF");
                if (!index.AllowPageLocks) withOptions.Add("ALLOW_PAGE_LOCKS = OFF");

                var extra = sources.IndexExtrasBy.GetValueOrDefault(Pair(objectId, index.IndexId));
                if (extra?.StatisticsNoRecompute == true) withOptions.Add("STATISTICS_NORECOMPUTE = ON");
                if (extra?.OptimizeForSequentialKey == true) withOptions.Add("OPTIMIZE_FOR_SEQUENTIAL_KEY = ON");
                if (withOptions.Count > 0)
                    sb.AppendLine().Append("    WITH (").Append(string.Join(", ", withOptions)).Append(')');
            }

            sb.Append(';');
            lines.Add(sb.ToString());
        }

        return lines;
    }

    // --- XML / spatial index ---

    /// <summary>
    /// XML ve spatial index'ler. Primary XML index secondary'lerden ÖNCE yazılmalı
    /// (secondary ona bağlı); ikisi de tablo oluştuktan sonra, ayrı batch'lerde.
    /// </summary>
    private static IEnumerable<string> WriteSpecialIndexes(
        ObjectKey key, int objectId, TableScriptSources sources)
    {
        var qualified = Quote(key.Schema, key.Name);
        var lines = new List<string>();

        foreach (var idx in (sources.XmlIndexesBy.GetValueOrDefault(objectId) ?? [])
                     .OrderByDescending(i => i.IsPrimary).ThenBy(i => i.Name, StringComparer.Ordinal))
        {
            lines.Add($"GO{Environment.NewLine}{SpecialIndexScript.Create(qualified, idx)}");
        }

        foreach (var idx in (sources.SpatialIndexesBy.GetValueOrDefault(objectId) ?? [])
                     .OrderBy(i => i.Name, StringComparer.Ordinal))
        {
            lines.Add($"GO{Environment.NewLine}{SpecialIndexScript.Create(qualified, idx)}");
        }

        return lines;
    }

    // --- full-text index ---

    /// <summary>
    /// Full-text index KEY INDEX'e (benzersiz index) bağlıdır, o yüzden index'lerden SONRA
    /// ve ayrı batch'te yazılır. CREATE her zaman AKTİF doğar; pasif index ayrıca kapatılır.
    /// </summary>
    private static IEnumerable<string> WriteFullText(ObjectKey key, int objectId, TableScriptSources sources)
    {
        if (!sources.FullTextBy.TryGetValue(objectId, out var ft)) return [];

        var qualified = Quote(key.Schema, key.Name);
        var lines = new List<string> { $"GO{Environment.NewLine}{FullTextScript.Create(qualified, ft)}" };
        if (!ft.IsEnabled) lines.Add($"GO{Environment.NewLine}{FullTextScript.Disable(qualified)}");
        return lines;
    }

    // --- pasif constraint'ler ---

    /// <summary>
    /// CREATE TABLE içindeki constraint'ler her zaman AKTİF doğar; pasif olanlar ancak
    /// tablo oluştuktan sonra kapatılabilir. Yazılmazsa hedefte constraint sessizce
    /// etkinleşir ve kaynakta bilerek kapatılmış bir kural devreye girer.
    /// </summary>
    private static IEnumerable<string> WriteConstraintState(
        ObjectKey key, int objectId, TableScriptSources sources)
    {
        var qualified = Quote(key.Schema, key.Name);
        var names = new List<string>();

        names.AddRange((sources.ChecksBy.GetValueOrDefault(objectId) ?? [])
            .Where(c => c.IsDisabled).Select(c => c.Name));
        names.AddRange((sources.ForeignKeysBy.GetValueOrDefault(objectId) ?? [])
            .Where(f => f.IsDisabled).Select(f => f.Name));

        names.Sort(StringComparer.Ordinal);
        return names.Select(n =>
            $"GO{Environment.NewLine}ALTER TABLE {qualified} NOCHECK CONSTRAINT [{n}];");
    }

    // --- kullanıcı istatistikleri ---

    /// <summary>
    /// Yeni tablonun script'ine CREATE STATISTICS satırlarını ekler. Index'lerin ardından
    /// gelir: ikisi de tablo oluştuktan sonra, ayrı batch'lerde çalışır.
    /// </summary>
    private static IEnumerable<string> WriteStatistics(
        ObjectKey key, int objectId, TableScriptSources sources, SnapshotOptions options)
    {
        var lines = new List<string>();
        if (options.IgnoreStatistics) return lines;

        var qualified = Quote(key.Schema, key.Name);

        foreach (var stat in (sources.StatisticsBy.GetValueOrDefault(objectId) ?? [])
                     .OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            var columns = (sources.StatisticColumnsBy.GetValueOrDefault(Pair(objectId, stat.StatsId)) ?? [])
                .OrderBy(c => c.StatsColumnId)
                .Select(c => ColumnName(sources, objectId, c.ColumnId))
                .ToList();
            if (columns.Count == 0) continue;

            var definition = new StatisticsDefinition(
                stat.Name, columns, stat.FilterDefinition, stat.NoRecompute, stat.IsIncremental);
            lines.Add($"GO{Environment.NewLine}{StatisticsScript.Create(qualified, definition)}");
        }

        return lines;
    }

    private static string KeyColumns(int objectId, int indexId, TableScriptSources sources)
    {
        var columns = sources.IndexColumnsBy.GetValueOrDefault(Pair(objectId, indexId)) ?? [];

        var keys = columns.Where(c => !c.IsIncluded && c.KeyOrdinal > 0).OrderBy(c => c.KeyOrdinal)
            .Select(c => $"[{ColumnName(sources, objectId, c.ColumnId)}] {(c.IsDescending ? "DESC" : "ASC")}");

        // Columnstore index'lerde kolonların key_ordinal'ı 0 ve included değildir.
        var unordered = columns.Where(c => !c.IsIncluded && c.KeyOrdinal == 0)
            .Select(c => $"[{ColumnName(sources, objectId, c.ColumnId)}]")
            .Order(StringComparer.Ordinal);

        return string.Join(", ", keys.Concat(unordered));
    }

    private static string IncludedColumns(int objectId, int indexId, TableScriptSources sources) =>
        string.Join(", ", (sources.IndexColumnsBy.GetValueOrDefault(Pair(objectId, indexId)) ?? [])
            .Where(c => c.IsIncluded)
            .Select(c => $"[{ColumnName(sources, objectId, c.ColumnId)}]")
            .Order(StringComparer.Ordinal));

    // --- yardımcılar ---

    private static string ColumnName(TableScriptSources sources, int objectId, int columnId) =>
        sources.ColumnNames.GetValueOrDefault(Pair(objectId, columnId), $"#{columnId}");

    private static string Quote(string schema, string name) => $"[{schema}].[{name}]";

    private static long Pair(int high, int low) => ((long)high << 32) | (uint)low;
}
