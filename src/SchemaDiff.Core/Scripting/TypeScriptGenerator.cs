using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record TypeScriptOptions
{
    /// <summary>Silinen tipleri DROP et. Varsayılan kapalı: tip başka objelerce
    /// kullanılıyorsa DROP patlar, bu yüzden bilinçli onay ister.</summary>
    public bool IncludeDrops { get; init; }

    public bool WrapInTransaction { get; init; } = true;

    public string? GeneratedAt { get; init; }

    public static readonly TypeScriptOptions Default = new();
}

public sealed record TypeScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<SkippedObject> Skipped)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>
/// Tablo-öncesi bağımsız objeler için dağıtım script'i üretir: kullanıcı tanımlı alias
/// tipler, table type'lar, sequence'lar ve synonym'ler.
///
/// Bu dilim tablo ve modül dilimlerinden ÖNCE çalışmalıdır — yeni bir tablo/prosedür yeni
/// bir tipi ya da sequence'ı (DEFAULT NEXT VALUE FOR) kullanabilir; önce var olmalı. Sıra:
/// sequence → alias tip → table type → synonym.
///
/// Bunlar ALTER edilemez (sequence kısmen edilebilir ama bağımlılık riski var): DEĞİŞEN
/// obje atlanır ve sebebi bildirilir. SİLİNEN objeler yalnızca
/// <see cref="TypeScriptOptions.IncludeDrops"/> ile ve varlık koruması içinde yazılır.
/// </summary>
public static class TypeScriptGenerator
{
    /// <summary>
    /// Bu üretecin ele aldığı obje sınıfları. Modül üreteci "kapsam dışı" listesini bundan
    /// kurar: liste elle kopyalandığında yeni bir sınıf eklenince script'e GİRİYOR ama
    /// başlıkta "kapsam dışı" diye görünüyordu — rapor script'le çelişiyordu.
    /// </summary>
    public static readonly IReadOnlySet<ObjectKind> HandledKinds = new HashSet<ObjectKind>
    {
        ObjectKind.UserDefinedType, ObjectKind.TableType,
        ObjectKind.Sequence, ObjectKind.Synonym,
        ObjectKind.PartitionFunction, ObjectKind.PartitionScheme,
        ObjectKind.FullTextCatalog,
    };

    public static TypeScriptResult Generate(
        CompareResult result, ISet<ObjectKey>? selection = null, TypeScriptOptions? options = null)
    {
        options ??= TypeScriptOptions.Default;

        var included = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();

        var creates = new List<ObjectKey>();
        var drops = new List<ObjectKey>();

        foreach (var diff in result.Differences)
        {
            if (!HandledKinds.Contains(diff.Key.Kind)) continue;
            if (selection is not null && !selection.Contains(diff.Key)) continue;

            switch (diff.Kind)
            {
                case DiffKind.Added: creates.Add(diff.Key); break;
                case DiffKind.Removed: drops.Add(diff.Key); break;
                case DiffKind.Changed:
                    // Tip ALTER edilemez; sequence/synonym da güvenli olsun diye drop+recreate önerilir.
                    skipped.Add(new SkippedObject(diff.Key,
                        "değişti — otomatik ALTER üretilmiyor; bağımlılıkları düşürüp elle drop+recreate gerekir"));
                    break;
            }
        }

        if (creates.Count == 0 && (!options.IncludeDrops || drops.Count == 0))
        {
            foreach (var key in drops)
                if (!options.IncludeDrops) skipped.Add(new SkippedObject(key, "DROP kapalı — obje hedefte kalacak"));
            if (creates.Count == 0)
                return new TypeScriptResult(string.Empty, included, skipped);
        }

        var sb = new StringBuilder(4096);
        WriteHeader(sb, result, options);

        if (options.WrapInTransaction)
        {
            sb.AppendLine("SET XACT_ABORT ON;");
            sb.AppendLine("GO");
            sb.AppendLine("BEGIN TRANSACTION;");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        // Sıra: sequence ve alias tipler önce (tablo default'ları/table type'lar kullanabilir),
        // sonra table type'lar, en son synonym'ler (bağımsız).
        foreach (var key in creates
                     .OrderBy(CreatePriority)
                     .ThenBy(k => k.Schema, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!result.Source.Objects.TryGetValue(key, out var snapshot)
                || string.IsNullOrWhiteSpace(snapshot.DisplayScript))
            {
                skipped.Add(new SkippedObject(key, "CREATE metni yok — snapshot display script'i gerekli"));
                continue;
            }

            sb.AppendLine($"PRINT N'Oluşturuluyor: {Describe(key)}';");
            sb.AppendLine($"IF {NotExistsCondition(key)}");
            sb.AppendLine("BEGIN");
            foreach (var line in snapshot.DisplayScript!.TrimEnd().Split('\n'))
                sb.Append("    ").AppendLine(line.TrimEnd('\r'));
            sb.AppendLine("END");
            sb.AppendLine("GO");
            sb.AppendLine();
            included.Add(key);
        }

        if (options.IncludeDrops)
        {
            // DROP'lar ters sırada: synonym → table type → alias tip → sequence.
            foreach (var key in drops
                         .OrderByDescending(CreatePriority)
                         .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            {
                sb.AppendLine($"PRINT N'Siliniyor: {Describe(key)}';");
                sb.AppendLine($"IF {ExistsCondition(key)}");
                sb.AppendLine($"    {DropStatement(key)}");
                sb.AppendLine("GO");
                sb.AppendLine();
                included.Add(key);
            }
        }

        if (options.WrapInTransaction)
        {
            sb.AppendLine("IF @@TRANCOUNT > 0 COMMIT TRANSACTION;");
            sb.AppendLine("GO");
        }

        return new TypeScriptResult(sb.ToString(), included, skipped);
    }

    private static void WriteHeader(StringBuilder sb, CompareResult result, TypeScriptOptions options)
    {
        sb.AppendLine("/* ---- 1) Tipler / sequence / synonym / partition -------------------------");
        sb.AppendLine("   Tablo ve modüllerden ÖNCE çalışır (bunlar bu tiplere bağlı olabilir).");
        sb.AppendLine("   ------------------------------------------------------------------------ */");
        sb.AppendLine();
    }

    // Sıra: partition function → scheme → sequence → alias tip → table type → synonym.
    // Scheme function'a, tablolar scheme'e bağlı olduğundan partition objeler en önce.
    private static int CreatePriority(ObjectKey key) => key.Kind switch
    {
        // Full-text katalog en önce: tabloların full-text index'i ona bağlı.
        ObjectKind.FullTextCatalog => -1,
        ObjectKind.PartitionFunction => 0,
        ObjectKind.PartitionScheme => 1,
        ObjectKind.Sequence => 2,
        ObjectKind.UserDefinedType => 3,
        ObjectKind.TableType => 4,
        ObjectKind.Synonym => 5,
        _ => 6,
    };

    private static string NotExistsCondition(ObjectKey key) => key.Kind switch
    {
        ObjectKind.Sequence => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'SO') IS NULL",
        ObjectKind.Synonym => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'SN') IS NULL",
        ObjectKind.PartitionFunction => $"NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.PartitionScheme => $"NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.FullTextCatalog => $"NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'{Escape(key.Name)}')",
        _ => $"TYPE_ID(N'[{key.Schema}].[{key.Name}]') IS NULL",
    };

    private static string ExistsCondition(ObjectKey key) => key.Kind switch
    {
        ObjectKind.Sequence => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'SO') IS NOT NULL",
        ObjectKind.Synonym => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'SN') IS NOT NULL",
        ObjectKind.PartitionFunction => $"EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.PartitionScheme => $"EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.FullTextCatalog => $"EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'{Escape(key.Name)}')",
        _ => $"TYPE_ID(N'[{key.Schema}].[{key.Name}]') IS NOT NULL",
    };

    private static string DropStatement(ObjectKey key) => key.Kind switch
    {
        ObjectKind.Sequence => $"DROP SEQUENCE [{key.Schema}].[{key.Name}];",
        ObjectKind.Synonym => $"DROP SYNONYM [{key.Schema}].[{key.Name}];",
        ObjectKind.PartitionFunction => $"DROP PARTITION FUNCTION [{key.Name}];",
        ObjectKind.PartitionScheme => $"DROP PARTITION SCHEME [{key.Name}];",
        ObjectKind.FullTextCatalog => $"DROP FULLTEXT CATALOG [{key.Name}];",
        _ => $"DROP TYPE [{key.Schema}].[{key.Name}];",
    };

    private static string Escape(string value) => value.Replace("'", "''");

    private static string Describe(ObjectKey key) => $"{key.Kind} [{key.Schema}].[{key.Name}]";
}
