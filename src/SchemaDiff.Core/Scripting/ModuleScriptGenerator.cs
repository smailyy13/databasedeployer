using System.Text;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record ScriptOptions
{
    /// <summary>Hedefte olup kaynakta olmayan modülleri DROP et.</summary>
    public bool IncludeDrops { get; init; } = true;

    /// <summary>Tümünü tek transaction'a sar — bir adım patlarsa hiçbiri kalmasın.</summary>
    public bool WrapInTransaction { get; init; } = true;

    /// <summary>Modüllerin ihtiyaç duyduğu ve hedefte olmayan şemaları oluştur.</summary>
    public bool CreateMissingSchemas { get; init; } = true;

    /// <summary>Başlığa yazılacak zaman damgası. Çağıran verir; üretim deterministik kalsın.</summary>
    public string? GeneratedAt { get; init; }

    /// <summary>
    /// Tablo değişiklikleri başka bir dilimde (TableScriptGenerator) ele alınıyor.
    /// Açıkken tablolar "kapsam dışı" listesine GİRMEZ — birleşik script'te başlığın
    /// "bu değişiklik uygulanmayacak" demesi yanıltıcı olurdu.
    /// </summary>
    public bool TablesHandledElsewhere { get; init; }

    /// <summary>
    /// Başka dilimlerde ele alınan obje sınıfları (roller, tipler vb.). Bunlar da
    /// "kapsam dışı" listesine girmez — birleşik script'te çift raporlama olmasın.
    /// </summary>
    public ISet<ObjectKind>? HandledElsewhere { get; init; }

    public static readonly ScriptOptions Default = new();
}

public sealed record SkippedObject(ObjectKey Key, string Reason);

public sealed record ScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<ObjectKey> OutOfScope,
    IReadOnlyList<SkippedObject> Skipped,
    bool HadDependencyCycle)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>
/// Modüller (view, prosedür, fonksiyon, trigger) ve gerekli şemalar için
/// çalıştırılabilir dağıtım script'i üretir.
///
/// KAPSAM BİLİNÇLİ OLARAK DAR: tablo, kolon, index ve constraint değişiklikleri
/// bu script'e GİRMEZ. Sebebi, o değişikliklerin veri kaybı riski taşıması ve
/// veri taşıma mantığı gerektirmesi. Kapsam dışı kalan her obje çıktıda ismiyle
/// listelenir — sessizce atlanmaz, çünkü "script'te yoktu" demek "değişiklik yoktu"
/// anlamına gelmemeli.
///
/// Modüllerde veri kaybı imkânsızdır; bu yüzden ilk ve en güvenli dilim burasıdır.
/// </summary>
public static class ModuleScriptGenerator
{
    private static readonly ObjectKind[] ScriptableKinds =
    [
        ObjectKind.View, ObjectKind.Procedure, ObjectKind.ScalarFunction,
        ObjectKind.InlineTableFunction, ObjectKind.TableFunction, ObjectKind.Trigger,
        ObjectKind.DdlTrigger,
    ];

    public static ScriptResult Generate(
        CompareResult result, ISet<ObjectKey>? selection = null, ScriptOptions? options = null)
    {
        options ??= ScriptOptions.Default;
        var comparer = ObjectKeyComparer.CaseInsensitive;

        var included = new List<ObjectKey>();
        var outOfScope = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();

        var schemasToAdd = new List<ObjectKey>();
        var drops = new List<ObjectKey>();
        var alters = new List<ObjectKey>();
        var creates = new List<ObjectKey>();

        foreach (var diff in result.Differences)
        {
            if (selection is not null && !selection.Contains(diff.Key)) continue;

            if (diff.Kind == DiffKind.Indeterminate)
            {
                skipped.Add(new SkippedObject(diff.Key, "karşılaştırılamadı — tanımı okunamıyor"));
                continue;
            }

            if (diff.Key.Kind == ObjectKind.Schema)
            {
                if (diff.Kind == DiffKind.Added && options.CreateMissingSchemas) schemasToAdd.Add(diff.Key);
                else outOfScope.Add(diff.Key);
                continue;
            }

            if (!ScriptableKinds.Contains(diff.Key.Kind))
            {
                // Başka dilimde ele alınan sınıflar (tablo, rol vb.) kapsam dışı sayılmaz.
                var handled = (diff.Key.Kind == ObjectKind.Table && options.TablesHandledElsewhere)
                    || (options.HandledElsewhere?.Contains(diff.Key.Kind) ?? false);
                if (!handled) outOfScope.Add(diff.Key);
                continue;
            }

            switch (diff.Kind)
            {
                case DiffKind.Removed when options.IncludeDrops: drops.Add(diff.Key); break;
                case DiffKind.Removed: outOfScope.Add(diff.Key); break;
                case DiffKind.Changed: alters.Add(diff.Key); break;
                case DiffKind.Added: creates.Add(diff.Key); break;
            }
        }

        // Yeni objeler bağımlılık sırasına dizilir; prosedürler için gerekmez
        // (SQL Server'ın deferred name resolution'ı) ama view ve fonksiyonlar için şart.
        var orderedCreates = TopologicalOrder(creates, result.Source, comparer, out var hadCycle);

        var sb = new StringBuilder(8192);
        WriteHeader(sb, result, options, outOfScope, hadCycle);

        if (options.WrapInTransaction)
        {
            sb.AppendLine("SET XACT_ABORT ON;");
            sb.AppendLine("SET NOCOUNT ON;");
            sb.AppendLine("GO");
            sb.AppendLine();
            sb.AppendLine("BEGIN TRANSACTION;");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        foreach (var schema in schemasToAdd)
        {
            sb.AppendLine($"PRINT N'Şema oluşturuluyor: [{schema.Name}]';");
            sb.AppendLine("GO");
            sb.AppendLine($"IF SCHEMA_ID(N'{Escape(schema.Name)}') IS NULL");
            sb.AppendLine($"    EXECUTE (N'CREATE SCHEMA [{schema.Name}]');");
            sb.AppendLine("GO");
            sb.AppendLine();
            included.Add(schema);
        }

        foreach (var key in drops.OrderBy(k => k.Kind).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"PRINT N'Siliniyor: {Describe(key)}';");
            sb.AppendLine("GO");
            if (key.Kind == ObjectKind.DdlTrigger)
            {
                // Veritabanı seviyesi DDL trigger: şema yok, sys.triggers'ta parent_class=0.
                sb.AppendLine($"IF EXISTS (SELECT 1 FROM sys.triggers WHERE parent_class = 0 AND name = N'{Escape(key.Name)}')");
                sb.AppendLine($"    DROP TRIGGER [{key.Name}] ON DATABASE;");
            }
            else
            {
                sb.AppendLine($"IF OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'{TypeCode(key.Kind)}') IS NOT NULL");
                sb.AppendLine($"    DROP {DropKeyword(key.Kind)} [{key.Schema}].[{key.Name}];");
            }
            sb.AppendLine("GO");
            sb.AppendLine();
            included.Add(key);
        }

        foreach (var key in orderedCreates)
            EmitModule(sb, key, result, isCreate: true, included, skipped);

        foreach (var key in alters.OrderBy(k => k.Kind).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            EmitModule(sb, key, result, isCreate: false, included, skipped);

        if (options.WrapInTransaction)
        {
            sb.AppendLine("IF @@TRANCOUNT > 0 COMMIT TRANSACTION;");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        sb.AppendLine($"PRINT N'Tamamlandı: {included.Count} obje uygulandı.';");
        sb.AppendLine("GO");

        return new ScriptResult(sb.ToString(), included, outOfScope, skipped, hadCycle);
    }

    // --- modül yazımı ---

    private static void EmitModule(
        StringBuilder sb, ObjectKey key, CompareResult result,
        bool isCreate, List<ObjectKey> included, List<SkippedObject> skipped)
    {
        if (!result.Source.Objects.TryGetValue(key, out var snapshot))
        {
            skipped.Add(new SkippedObject(key, "kaynakta bulunamadı"));
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.DisplayScript))
        {
            skipped.Add(new SkippedObject(key,
                "tanım metni yok — şifrelenmiş obje ya da VIEW DEFINITION yetkisi eksik"));
            return;
        }

        // Trigger'ın bağlı olduğu tablo hedefte yoksa CREATE patlar. Tablolar bu
        // script'in kapsamı dışında olduğu için burada durup açıkça söylüyoruz.
        if (key.Kind == ObjectKind.Trigger && snapshot.Parent is { } parent
            && !result.Target.Objects.ContainsKey(parent))
        {
            skipped.Add(new SkippedObject(key,
                $"bağlı olduğu {parent} hedefte yok — önce tablo değişiklikleri uygulanmalı"));
            return;
        }

        var body = snapshot.DisplayScript!.TrimEnd();

        if (!isCreate)
        {
            var altered = ToAlter(body, snapshot.UsesQuotedIdentifier ?? true);
            if (altered is null)
            {
                skipped.Add(new SkippedObject(key, "tanım parse edilemedi, CREATE→ALTER dönüşümü yapılamadı"));
                return;
            }
            body = altered;
        }

        // SET seçenekleri modülün oluşturulduğu andaki değerleriyle kurulmalı;
        // CREATE/ALTER kendi batch'inin ilk ifadesi olmak zorunda olduğu için ayrı batch.
        sb.AppendLine($"PRINT N'{(isCreate ? "Oluşturuluyor" : "Değiştiriliyor")}: {Describe(key)}';");
        sb.AppendLine("GO");
        sb.AppendLine($"SET ANSI_NULLS {OnOff(snapshot.UsesAnsiNulls)};");
        sb.AppendLine($"SET QUOTED_IDENTIFIER {OnOff(snapshot.UsesQuotedIdentifier)};");
        sb.AppendLine("GO");
        sb.AppendLine(body);
        sb.AppendLine("GO");
        // CREATE/ALTER TRIGGER trigger'ı aktif eder; source'ta pasifse durumu koru.
        if (key.Kind == ObjectKind.DdlTrigger && snapshot.IsDisabled == true)
        {
            sb.AppendLine($"DISABLE TRIGGER [{key.Name}] ON DATABASE;");
            sb.AppendLine("GO");
        }
        sb.AppendLine();

        included.Add(key);
    }

    /// <summary>
    /// Tanımın başındaki CREATE anahtar kelimesini ALTER ile değiştirir.
    /// Metin araması DEĞİL token araması: yorumda ya da string içinde geçen "create"
    /// yanlışlıkla değiştirilmesin.
    /// </summary>
    internal static string? ToAlter(string definition, bool quotedIdentifiers)
    {
        var parser = new TSql160Parser(quotedIdentifiers);
        IList<ParseError> errors;
        IList<TSqlParserToken> tokens;
        using (var reader = new StringReader(definition)) tokens = parser.GetTokenStream(reader, out errors);

        if (errors.Count > 0) return null;

        foreach (var token in tokens)
        {
            if (token.TokenType != TSqlTokenType.Create) continue;
            return string.Concat(
                definition.AsSpan(0, token.Offset),
                "ALTER",
                definition.AsSpan(token.Offset + token.Text.Length));
        }

        return null;
    }

    // --- sıralama ---

    /// <summary>
    /// Yeni objeleri referans sırasına dizer: bir obje, referans verdiği objelerden
    /// sonra oluşturulur. Döngü varsa kalanlar ad sırasına düşer ve çağırana bildirilir.
    /// </summary>
    private static List<ObjectKey> TopologicalOrder(
        List<ObjectKey> nodes, DatabaseSnapshot source, ObjectKeyComparer comparer, out bool hadCycle)
    {
        var pending = new HashSet<ObjectKey>(nodes, comparer);
        var ordered = new List<ObjectKey>(nodes.Count);
        var done = new HashSet<ObjectKey>(comparer);
        var visiting = new HashSet<ObjectKey>(comparer);
        var cycle = false;

        void Visit(ObjectKey key)
        {
            if (done.Contains(key)) return;
            if (!visiting.Add(key)) { cycle = true; return; }

            foreach (var reference in source.References.GetValueOrDefault(key) ?? [])
                if (pending.Contains(reference)) Visit(reference);

            visiting.Remove(key);
            if (done.Add(key)) ordered.Add(key);
        }

        foreach (var key in nodes.OrderBy(k => k.Kind).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            Visit(key);

        hadCycle = cycle;
        return ordered;
    }

    // --- başlık ---

    private static void WriteHeader(
        StringBuilder sb, CompareResult result, ScriptOptions options,
        List<ObjectKey> outOfScope, bool hadCycle)
    {
        sb.AppendLine("/* ---- 3) Modüller --------------------------------------------------------");
        sb.AppendLine("   Şema (CREATE SCHEMA), view, prosedür, fonksiyon, trigger. Veri kaybı yoktur.");

        if (outOfScope.Count > 0)
        {
            sb.AppendLine($"   !! KAPSAM DIŞI {outOfScope.Count} DEĞİŞİKLİK VAR — bu script bunları UYGULAMAZ.");
            sb.AppendLine("   Tablo/kolon/index/constraint değişiklikleri ayrıca ele alınmalıdır:");
            sb.AppendLine();
            foreach (var key in outOfScope
                         .OrderBy(k => k.Kind).ThenBy(k => k.Schema, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
                sb.AppendLine($"        {key}");
            sb.AppendLine();
        }

        if (hadCycle)
            sb.AppendLine("   !! Yeni objeler arasında döngüsel referans var; sıralama tam garanti " +
                          "edilemedi. Hata verirse ilgili objeyi elle uygulayın.");
        sb.AppendLine("   ------------------------------------------------------------------------ */");
        sb.AppendLine();
    }

    // --- yardımcılar ---

    private static string OnOff(bool? value) => value == false ? "OFF" : "ON";

    private static string Escape(string value) => value.Replace("'", "''");

    private static string Describe(ObjectKey key) => $"{key.Kind} [{key.Schema}].[{Escape(key.Name)}]";

    private static string TypeCode(ObjectKind kind) => kind switch
    {
        ObjectKind.View => "V",
        ObjectKind.Procedure => "P",
        ObjectKind.ScalarFunction => "FN",
        ObjectKind.InlineTableFunction => "IF",
        ObjectKind.TableFunction => "TF",
        ObjectKind.Trigger => "TR",
        _ => "U",
    };

    private static string DropKeyword(ObjectKind kind) => kind switch
    {
        ObjectKind.View => "VIEW",
        ObjectKind.Procedure => "PROCEDURE",
        ObjectKind.Trigger => "TRIGGER",
        _ => "FUNCTION",
    };
}
