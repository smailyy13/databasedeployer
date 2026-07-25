using SchemaDiff.Core.Model;

namespace SchemaDiff.Tests;

/// <summary>
/// Testlerde snapshot kurmak için kısa yol. Gerçek çekim katalogdan gelir; burada
/// karşılaştırma ve script üretimi mantığını izole test etmek için elle kuruyoruz.
/// </summary>
internal static class TestFactory
{
    public static ObjectKey Table(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.Table);

    public static ObjectKey View(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.View);

    public static ObjectKey Proc(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.Procedure);

    public static ObjectKey Trigger(string name, string schema = "dbo") =>
        new(schema, name, ObjectKind.Trigger);

    public static ObjectKey Schema(string name) =>
        new(string.Empty, name, ObjectKind.Schema);

    public static ObjectSnapshot Obj(
        ObjectKey key,
        UInt128 hash,
        string? displayScript = null,
        IReadOnlyList<ColumnInfo>? columns = null,
        long? rowCount = null,
        bool incomparable = false,
        ObjectKey? parent = null,
        bool ansiNulls = true,
        bool quotedIdentifier = true)
    {
        var snapshot = new ObjectSnapshot
        {
            Key = key,
            Hash = hash,
            DisplayScript = displayScript,
            Canonical = displayScript ?? string.Empty,
            Columns = columns,
            RowCount = rowCount,
            Incomparable = incomparable,
            IncomparableReason = incomparable ? "tanım okunamıyor" : null,
            Parent = parent,
            UsesAnsiNulls = ansiNulls,
            UsesQuotedIdentifier = quotedIdentifier,
        };
        return snapshot;
    }

    public static DatabaseSnapshot Database(
        string database,
        IEnumerable<ObjectSnapshot> objects,
        Dictionary<ObjectKey, List<ObjectKey>>? references = null,
        string server = "TESTSRV")
    {
        var dict = new Dictionary<ObjectKey, ObjectSnapshot>(ObjectKeyComparer.CaseInsensitive);
        foreach (var o in objects) dict[o.Key] = o;

        return new DatabaseSnapshot
        {
            Server = server,
            Database = database,
            Objects = dict,
            References = references ?? [],
        };
    }

    public static ColumnInfo Column(
        string name,
        string typeName = "int",
        short maxLength = 4,
        byte precision = 10,
        byte scale = 0,
        bool nullable = true,
        bool identity = false,
        bool computed = false,
        string? defaultDefinition = null,
        string? collation = null,
        string? defaultName = null,
        bool defaultIsSystemNamed = false) =>
        new(name, "sys", typeName, maxLength, precision, scale, nullable, collation, identity, computed,
            defaultDefinition, defaultName, defaultIsSystemNamed);
}
