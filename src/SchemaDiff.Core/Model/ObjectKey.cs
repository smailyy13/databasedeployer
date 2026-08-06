namespace SchemaDiff.Core.Model;

/// <summary>Şema objesi türü. sys.objects.type kodlarıyla eşleşir.</summary>
public enum ObjectKind
{
    Unknown = 0,
    Schema,
    Table,
    View,
    Procedure,
    ScalarFunction,
    InlineTableFunction,
    TableFunction,
    Trigger,
    Synonym,
    Sequence,
    Role,
    User,
    UserDefinedType,
    TableType,
    PartitionFunction,
    PartitionScheme,
    DdlTrigger,
    Database,
}

public static class ObjectKindMap
{
    /// <summary>sys.objects.type (char(2), sondaki boşluk kırpılmış olmalı) → ObjectKind.</summary>
    public static ObjectKind FromSysType(string type) => type switch
    {
        "U" => ObjectKind.Table,
        "V" => ObjectKind.View,
        "P" => ObjectKind.Procedure,
        "FN" => ObjectKind.ScalarFunction,
        "IF" => ObjectKind.InlineTableFunction,
        "TF" => ObjectKind.TableFunction,
        "TR" => ObjectKind.Trigger,
        "SN" => ObjectKind.Synonym,
        "SO" => ObjectKind.Sequence,
        _ => ObjectKind.Unknown,
    };

    /// <summary>Gövdesi sys.sql_modules'ta duran objeler.</summary>
    public static bool IsModule(this ObjectKind kind) => kind is
        ObjectKind.View or ObjectKind.Procedure or ObjectKind.ScalarFunction or
        ObjectKind.InlineTableFunction or ObjectKind.TableFunction or ObjectKind.Trigger;
}

public readonly record struct ObjectKey(string Schema, string Name, ObjectKind Kind)
{
    public override string ToString() => Kind switch
    {
        ObjectKind.Schema => $"SCHEMA [{Name}]",
        ObjectKind.Role => $"ROLE [{Name}]",
        ObjectKind.User => $"USER [{Name}]",
        ObjectKind.PartitionFunction => $"PARTITION FUNCTION [{Name}]",
        ObjectKind.PartitionScheme => $"PARTITION SCHEME [{Name}]",
        ObjectKind.DdlTrigger => $"DDL TRIGGER [{Name}]",
        ObjectKind.Database => "DATABASE",
        _ => $"{Kind} [{Schema}].[{Name}]",
    };
}

/// <summary>
/// İsim karşılaştırması hedef veritabanının collation'ına bağlıdır; SQL Server varsayılanı
/// case-insensitive olduğundan varsayılan karşılaştırıcı da öyledir.
/// </summary>
public sealed class ObjectKeyComparer : IEqualityComparer<ObjectKey>
{
    public static readonly ObjectKeyComparer CaseInsensitive = new(StringComparer.OrdinalIgnoreCase);
    public static readonly ObjectKeyComparer CaseSensitive = new(StringComparer.Ordinal);

    private readonly StringComparer _cmp;
    private ObjectKeyComparer(StringComparer cmp) => _cmp = cmp;

    public bool Equals(ObjectKey x, ObjectKey y) =>
        x.Kind == y.Kind && _cmp.Equals(x.Schema, y.Schema) && _cmp.Equals(x.Name, y.Name);

    public int GetHashCode(ObjectKey obj) =>
        HashCode.Combine((int)obj.Kind, _cmp.GetHashCode(obj.Schema), _cmp.GetHashCode(obj.Name));
}
