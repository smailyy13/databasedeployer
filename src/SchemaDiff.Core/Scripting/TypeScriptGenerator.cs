using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record TypeScriptOptions
{
    /// <summary>Silinen tipleri DROP et. Varsayılan kapalı: tip başka objelerce
    /// kullanılıyorsa DROP patlar, bu yüzden bilinçli onay ister.</summary>
    public bool IncludeDrops { get; init; }

    public bool WrapInTransaction { get; init; } = true;

    /// <summary>
    /// Değişen table type'ı yeniden kur: bağımlı modülleri DÜŞÜR → tipi drop+create →
    /// modülleri geri kur. Varsayılan KAPALI, çünkü bir prosedürü düşürüp geri kuramamak
    /// (tanımı okunamıyorsa) onarılamaz. Açıkken bile bağımlıların TAMAMI okunabilir
    /// değilse üretim yapılmaz, sebebiyle atlanır.
    /// </summary>
    public bool RecreateChangedTableTypes { get; init; }

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
        ObjectKind.XmlSchemaCollection,
        ObjectKind.FullTextStoplist,
        ObjectKind.LegacyRuleDefault,
    };

    public static TypeScriptResult Generate(
        CompareResult result, ISet<ObjectKey>? selection = null, TypeScriptOptions? options = null)
    {
        options ??= TypeScriptOptions.Default;

        var included = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();

        var creates = new List<ObjectKey>();
        var drops = new List<ObjectKey>();
        var stoplistChanges = new List<ObjectKey>();
        var tableTypeRecreates = new List<ObjectKey>();

        foreach (var diff in result.Differences)
        {
            if (!HandledKinds.Contains(diff.Key.Kind)) continue;
            if (selection is not null && !selection.Contains(diff.Key)) continue;

            switch (diff.Kind)
            {
                case DiffKind.Added: creates.Add(diff.Key); break;
                case DiffKind.Removed: drops.Add(diff.Key); break;
                case DiffKind.Changed when diff.Key.Kind == ObjectKind.TableType
                                           && options.RecreateChangedTableTypes:
                    tableTypeRecreates.Add(diff.Key);
                    break;

                case DiffKind.Changed when diff.Key.Kind == ObjectKind.TableType:
                    skipped.Add(new SkippedObject(diff.Key,
                        "değişti — table type ALTER edilemez. Bağımlı modülleri düşürüp yeniden " +
                        "kuran script için 'Değişen table type'ları yeniden kur' seçeneğini açın."));
                    break;

                case DiffKind.Changed when diff.Key.Kind == ObjectKind.FullTextStoplist:
                    // Stoplist tek istisna: SQL Server kelime seviyesinde ADD/DROP veriyor,
                    // yani değişim TAM ve GÜVENLİ üretilebilir — drop+recreate gereksiz.
                    stoplistChanges.Add(diff.Key);
                    break;

                case DiffKind.Changed:
                    // Tip ALTER edilemez; sequence/synonym da güvenli olsun diye drop+recreate önerilir.
                    skipped.Add(new SkippedObject(diff.Key,
                        "değişti — otomatik ALTER üretilmiyor; bağımlılıkları düşürüp elle drop+recreate gerekir"));
                    break;
            }
        }

        if (creates.Count == 0 && stoplistChanges.Count == 0 && tableTypeRecreates.Count == 0
            && (!options.IncludeDrops || drops.Count == 0))
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

        foreach (var key in tableTypeRecreates
                     .OrderBy(k => k.Schema, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (RecreateTableType(sb, key, result) is { } reason) skipped.Add(new SkippedObject(key, reason));
            else included.Add(key);
        }

        foreach (var key in stoplistChanges.OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            var statements = StoplistWordDiff(key, result);
            if (statements.Count == 0)
            {
                skipped.Add(new SkippedObject(key, "kelime listesi okunamadı — değişiklik üretilemedi"));
                continue;
            }

            sb.AppendLine($"PRINT N'Güncelleniyor: {Describe(key)}';");
            foreach (var statement in statements) sb.AppendLine(statement);
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
                sb.AppendLine($"    {DropStatement(key, result.Target.Objects.GetValueOrDefault(key))}");
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

    /// <summary>
    /// Değişen table type'ı yeniden kurar: bağımlı modülleri düşür → tipi drop+create →
    /// modülleri KAYNAK tanımıyla geri kur.
    ///
    /// ÖN KOŞUL katı: bağımlı modüllerin TAMAMININ tanımı okunabilir olmalı. Okunamayan tek
    /// bir modül varsa hiçbir şey üretilmez — düşürüp geri kuramamak onarılamaz bir hatadır.
    /// </summary>
    /// <returns>Üretilemediyse sebebi, üretildiyse null.</returns>
    private static string? RecreateTableType(StringBuilder sb, ObjectKey key, CompareResult result)
    {
        if (!result.Source.Objects.TryGetValue(key, out var source)
            || string.IsNullOrWhiteSpace(source.DisplayScript))
            return "CREATE metni yok — snapshot display script'i gerekli";

        result.Target.Objects.TryGetValue(key, out var target);

        // Hedefte var olan bağımlılar düşürülür, kaynaktakiler geri kurulur; birleşimin
        // tamamı okunabilir olmalı.
        var toDrop = target?.DependentModules ?? [];
        var toCreate = source.DependentModules ?? [];

        foreach (var dependent in toDrop.Concat(toCreate).Distinct())
        {
            if (!result.Source.Objects.TryGetValue(dependent, out var module)
                || string.IsNullOrWhiteSpace(module.DisplayScript) || module.Incomparable)
            {
                return $"bağımlı modül {dependent} geri kurulamaz (tanımı okunamıyor) — " +
                       "tip yeniden kurulmadı; elle uygulayın";
            }
        }

        sb.AppendLine($"PRINT N'Yeniden kuruluyor: {Describe(key)}';");
        sb.AppendLine("GO");
        sb.AppendLine();

        foreach (var dependent in toDrop)
        {
            sb.AppendLine($"IF OBJECT_ID(N'[{dependent.Schema}].[{dependent.Name}]') IS NOT NULL");
            sb.AppendLine($"    DROP {ModuleVerb(dependent.Kind)} [{dependent.Schema}].[{dependent.Name}];");
        }
        sb.AppendLine($"IF {ExistsCondition(key)}");
        sb.AppendLine($"    {DropStatement(key, result.Target.Objects.GetValueOrDefault(key))}");
        sb.AppendLine("GO");
        sb.AppendLine();

        sb.AppendLine(source.DisplayScript!.TrimEnd());
        sb.AppendLine("GO");
        sb.AppendLine();

        foreach (var dependent in toCreate)
        {
            var module = result.Source.Objects[dependent];
            // Modülün özgün SET seçenekleri korunmalı: aksi hâlde davranışı değişir.
            sb.AppendLine($"SET ANSI_NULLS {(module.UsesAnsiNulls == false ? "OFF" : "ON")};");
            sb.AppendLine($"SET QUOTED_IDENTIFIER {(module.UsesQuotedIdentifier == false ? "OFF" : "ON")};");
            sb.AppendLine("GO");
            sb.AppendLine(module.DisplayScript!.TrimEnd());
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        return null;
    }

    private static string ModuleVerb(ObjectKind kind) => kind switch
    {
        ObjectKind.Procedure => "PROCEDURE",
        ObjectKind.Trigger => "TRIGGER",
        ObjectKind.View => "VIEW",
        _ => "FUNCTION",
    };

    /// <summary>
    /// Stoplist'in kelime farkı: kaynakta olup hedefte olmayanlar ADD, tersi DROP.
    /// Kelime + dil birlikte kimliktir (aynı kelime farklı dilde ayrı kayıttır).
    /// </summary>
    private static List<string> StoplistWordDiff(ObjectKey key, CompareResult result)
    {
        var statements = new List<string>();
        if (!result.Source.Objects.TryGetValue(key, out var source)) return statements;
        if (!result.Target.Objects.TryGetValue(key, out var target)) return statements;
        if (source.Stopwords is null || target.Stopwords is null) return statements;

        var sourceWords = source.Stopwords.ToHashSet();
        var targetWords = target.Stopwords.ToHashSet();

        statements.AddRange(source.Stopwords.Where(w => !targetWords.Contains(w))
            .Select(w => Extraction.SnapshotBuilder.StopwordStatement(key.Name, w, add: true)));
        statements.AddRange(target.Stopwords.Where(w => !sourceWords.Contains(w))
            .Select(w => Extraction.SnapshotBuilder.StopwordStatement(key.Name, w, add: false)));

        return statements;
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
        // XML schema collection en önce: TİPLİ XML KOLONLARI ona bağlı, yani tablo
        // CREATE'i onsuz patlar. Sonra full-text katalog (index'i ona bağlı).
        ObjectKind.XmlSchemaCollection => -3,
        // Stoplist katalogdan da önce: full-text index ikisine birden bağlı.
        ObjectKind.FullTextStoplist => -2,
        ObjectKind.FullTextCatalog => -1,
        ObjectKind.PartitionFunction => 0,
        ObjectKind.PartitionScheme => 1,
        ObjectKind.Sequence => 2,
        ObjectKind.UserDefinedType => 3,
        ObjectKind.TableType => 4,
        ObjectKind.Synonym => 5,
        ObjectKind.LegacyRuleDefault => 6,
        _ => 6,
    };

    private static string NotExistsCondition(ObjectKey key) => key.Kind switch
    {
        ObjectKind.Sequence => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'SO') IS NULL",
        ObjectKind.Synonym => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'SN') IS NULL",
        ObjectKind.PartitionFunction => $"NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.PartitionScheme => $"NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.FullTextCatalog => $"NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.FullTextStoplist => $"NOT EXISTS (SELECT 1 FROM sys.fulltext_stoplists WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.XmlSchemaCollection => $"NOT EXISTS (SELECT 1 FROM sys.xml_schema_collections AS c INNER JOIN sys.schemas AS s ON s.schema_id = c.schema_id WHERE s.name = N'{Escape(key.Schema)}' AND c.name = N'{Escape(key.Name)}')",
        ObjectKind.LegacyRuleDefault => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]') IS NULL",
        _ => $"TYPE_ID(N'[{key.Schema}].[{key.Name}]') IS NULL",
    };

    private static string ExistsCondition(ObjectKey key) => key.Kind switch
    {
        ObjectKind.Sequence => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'SO') IS NOT NULL",
        ObjectKind.Synonym => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'SN') IS NOT NULL",
        ObjectKind.PartitionFunction => $"EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.PartitionScheme => $"EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.FullTextCatalog => $"EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.FullTextStoplist => $"EXISTS (SELECT 1 FROM sys.fulltext_stoplists WHERE name = N'{Escape(key.Name)}')",
        ObjectKind.XmlSchemaCollection => $"EXISTS (SELECT 1 FROM sys.xml_schema_collections AS c INNER JOIN sys.schemas AS s ON s.schema_id = c.schema_id WHERE s.name = N'{Escape(key.Schema)}' AND c.name = N'{Escape(key.Name)}')",
        ObjectKind.LegacyRuleDefault => $"OBJECT_ID(N'[{key.Schema}].[{key.Name}]') IS NOT NULL",
        _ => $"TYPE_ID(N'[{key.Schema}].[{key.Name}]') IS NOT NULL",
    };

    /// <summary>
    /// Legacy objede RULE ile DEFAULT'un DROP fiili farklıdır ve ikisi de tek ObjectKind
    /// altındadır; tür snapshot'ın kanonik parçasından okunur.
    /// </summary>
    private static string DropStatement(ObjectKey key, ObjectSnapshot? snapshot = null)
    {
        if (key.Kind == ObjectKind.LegacyRuleDefault)
        {
            var isDefault = snapshot?.PartCanonical.GetValueOrDefault("definition", string.Empty)
                .Contains("type=D", StringComparison.Ordinal) == true;
            return $"DROP {(isDefault ? "DEFAULT" : "RULE")} [{key.Schema}].[{key.Name}];";
        }

        return DropByKind(key);
    }

    private static string DropByKind(ObjectKey key) => key.Kind switch
    {
        ObjectKind.Sequence => $"DROP SEQUENCE [{key.Schema}].[{key.Name}];",
        ObjectKind.Synonym => $"DROP SYNONYM [{key.Schema}].[{key.Name}];",
        ObjectKind.PartitionFunction => $"DROP PARTITION FUNCTION [{key.Name}];",
        ObjectKind.PartitionScheme => $"DROP PARTITION SCHEME [{key.Name}];",
        ObjectKind.FullTextCatalog => $"DROP FULLTEXT CATALOG [{key.Name}];",
        ObjectKind.FullTextStoplist => $"DROP FULLTEXT STOPLIST [{key.Name}];",
        ObjectKind.XmlSchemaCollection => $"DROP XML SCHEMA COLLECTION [{key.Schema}].[{key.Name}];",
        _ => $"DROP TYPE [{key.Schema}].[{key.Name}];",
    };

    private static string Escape(string value) => value.Replace("'", "''");

    private static string Describe(ObjectKey key) => $"{key.Kind} [{key.Schema}].[{key.Name}]";
}
