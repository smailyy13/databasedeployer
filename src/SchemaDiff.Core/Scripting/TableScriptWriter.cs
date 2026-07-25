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
    public required Dictionary<int, ObjectKey> KeyById { get; init; }
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
        var typeWidth = columns.Count == 0 ? 0 : columns.Max(c => FormatType(c).Length);

        foreach (var column in columns.OrderBy(c => c.ColumnId))
            body.Add(WriteColumn(column, nameWidth, typeWidth, options));

        body.AddRange(WriteTableConstraints(objectId, sources, options));

        for (var i = 0; i < body.Count; i++)
            sb.Append(body[i]).AppendLine(i < body.Count - 1 ? "," : string.Empty);

        sb.AppendLine(");");

        foreach (var line in WriteIndexes(key, objectId, sources, options))
        {
            sb.AppendLine();
            sb.Append(line);
        }

        return sb.ToString().TrimEnd();
    }

    // --- kolonlar ---

    private static string WriteColumn(ColumnRow column, int nameWidth, int typeWidth, SnapshotOptions options)
    {
        var name = $"[{column.Name}]".PadRight(nameWidth);

        if (column.IsComputed)
        {
            var persisted = column.ComputedIsPersisted == true ? " PERSISTED" : string.Empty;
            return $"    {name} AS {column.ComputedDefinition}{persisted}";
        }

        var sb = new StringBuilder("    ");
        sb.Append(name).Append(' ').Append(FormatType(column).PadRight(typeWidth));

        if (column.Collation is not null && !options.IgnoreCollation)
            sb.Append(" COLLATE ").Append(column.Collation);

        if (column.IsIdentity)
        {
            sb.Append(" IDENTITY");
            if (!options.IgnoreIdentitySeed)
                sb.Append(" (").Append(column.IdentitySeed).Append(", ").Append(column.IdentityIncrement).Append(')');
        }

        sb.Append(column.IsNullable ? " NULL" : " NOT NULL");

        if (column.DefaultDefinition is not null)
        {
            var named = !(options.IgnoreSystemNamedConstraints && column.DefaultIsSystemNamed == true);
            if (named && column.DefaultName is not null) sb.Append(" CONSTRAINT [").Append(column.DefaultName).Append(']');
            sb.Append(" DEFAULT ").Append(column.DefaultDefinition);
        }

        return sb.ToString();
    }

    private static string FormatType(ColumnRow column)
    {
        // Kullanıcı tanımlı tipler şema nitelemesiyle yazılır.
        if (!string.Equals(column.TypeSchema, "sys", StringComparison.OrdinalIgnoreCase))
            return Quote(column.TypeSchema, column.TypeName);

        var upper = column.TypeName.ToUpperInvariant();
        var length = column.MaxLength.ToString(CultureInfo.InvariantCulture);

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
            lines.Add($"    {prefix}CHECK {check.Definition}");
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

            if (!options.IgnoreIndexPhysicalOptions && index.FillFactor > 0)
                sb.AppendLine().Append("    WITH (FILLFACTOR = ").Append(index.FillFactor).Append(')');

            sb.Append(';');
            lines.Add(sb.ToString());
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
