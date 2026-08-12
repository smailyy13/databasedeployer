namespace SchemaDiff.Core.Model;

/// <summary>
/// Obje türünün arayüzde görünen adı ve bu adın türe GERİ çevrimi.
///
/// İki yön bilerek aynı dosyada: ad ile çevrim ayrı yerlerde durduğunda sessizce ayrışıyor.
/// Nitekim "User-Defined Type" ve "Full-Text Catalog" tam da bu yüzden çözülemiyordu —
/// çözülemeyen tür, kullanıcı kutusunu işaretlese bile script'e girmez ve bunu fark etmesi
/// imkânsızdır. <c>ObjectKindLabelTests</c> her tür için gidiş-dönüşü doğrular.
///
/// Adlar SSDT çıktısıyla aynı okunsun diye İngilizce.
/// </summary>
public static class ObjectKindLabels
{
    public static string Display(ObjectKind kind) => kind switch
    {
        ObjectKind.Schema => "Schema",
        ObjectKind.Table => "Table",
        ObjectKind.View => "View",
        ObjectKind.Procedure => "Procedure",
        ObjectKind.ScalarFunction => "Scalar Function",
        ObjectKind.InlineTableFunction => "Inline Function",
        ObjectKind.TableFunction => "Table Function",
        ObjectKind.Trigger => "Trigger",
        ObjectKind.Synonym => "Synonym",
        ObjectKind.Sequence => "Sequence",
        ObjectKind.Role => "Role",
        ObjectKind.User => "User",
        ObjectKind.UserDefinedType => "User-Defined Type",
        ObjectKind.TableType => "Table Type",
        ObjectKind.PartitionFunction => "Partition Function",
        ObjectKind.PartitionScheme => "Partition Scheme",
        ObjectKind.DdlTrigger => "DDL Trigger",
        ObjectKind.FullTextCatalog => "Full-Text Catalog",
        ObjectKind.FullTextStoplist => "Full-Text Stoplist",
        ObjectKind.XmlSchemaCollection => "XML Schema Collection",
        ObjectKind.Database => "Database",
        _ => kind.ToString(),
    };

    /// <summary>
    /// Görünen adı türe çevirir. Enum adına birebir uymayanlar (kısaltılmış "Inline Function"
    /// gibi) açık eşlemeyle; ötekiler boşluk ve TİRE atılarak çözülür.
    /// </summary>
    public static ObjectKind Resolve(string label) => label switch
    {
        "Scalar Function" => ObjectKind.ScalarFunction,
        "Inline Function" => ObjectKind.InlineTableFunction,
        "Table Function" => ObjectKind.TableFunction,
        _ => Parse(label),
    };

    private static ObjectKind Parse(string label)
    {
        var normalized = label.Replace(" ", string.Empty).Replace("-", string.Empty);
        return Enum.TryParse<ObjectKind>(normalized, ignoreCase: true, out var kind)
            ? kind
            : ObjectKind.Unknown;
    }
}
