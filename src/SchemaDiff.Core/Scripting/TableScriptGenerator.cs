using System.Globalization;
using System.Text;
using SchemaDiff.Core.Model;

namespace SchemaDiff.Core.Scripting;

public sealed record TableScriptOptions
{
    /// <summary>Hedefte olup kaynakta olmayan tabloları DROP et. Veri kaybı — <see cref="AllowDataLoss"/> şart.</summary>
    public bool IncludeDrops { get; init; } = true;

    /// <summary>
    /// Veri kaybı riski taşıyan adımları (kolon silme, tip daraltma, tablo silme) script'e KOY.
    /// Varsayılan kapalı: bu adımlar üretilir ama ayrı listede raporlanır, script'e girmez.
    /// Kuveyt Türk prosedürü veri olan tabloda değişikliğe izin vermez — bu yüzden bilinçli onay şart.
    /// </summary>
    public bool AllowDataLoss { get; init; }

    /// <summary>Tümünü tek transaction'a sar.</summary>
    public bool WrapInTransaction { get; init; } = true;

    /// <summary>
    /// Yeni CHECK/FOREIGN KEY constraint'leri mevcut veriye karşı doğrula (WITH CHECK).
    /// Kapalıysa WITH NOCHECK üretilir: constraint dolu tabloya eklenebilir ama mevcut
    /// satırlar doğrulanmaz (constraint "not trusted" olur). SSDT: "Script validation for
    /// new constraints".
    /// </summary>
    public bool ValidateNewConstraints { get; init; } = true;

    /// <summary>Başlığa yazılacak zaman damgası. Çağıran verir; üretim deterministik kalsın.</summary>
    public string? GeneratedAt { get; init; }

    public static readonly TableScriptOptions Default = new();
}

/// <summary>Veri kaybı riski nedeniyle script'e alınmayan (ya da onayla alınan) adım.</summary>
public sealed record GatedAction(ObjectKey Table, string Column, string Description, string Sql);

public sealed record TableScriptResult(
    string Sql,
    IReadOnlyList<ObjectKey> Included,
    IReadOnlyList<SkippedObject> Skipped,
    IReadOnlyList<GatedAction> DataLossActions,
    bool HadDependencyCycle)
{
    public bool IsEmpty => Included.Count == 0;
}

/// <summary>
/// Tablo değişiklikleri için dağıtım script'i üretir: yeni tabloların CREATE'i, silinen
/// tabloların DROP'u ve değişen tablolarda kolon seviyesi ADD / ALTER / DROP COLUMN.
///
/// GÜVENLİK KADEMESİ:
///   • Additive değişiklikler (nullable kolon ekleme, tip genişletme) doğrudan script'e girer.
///   • Veri kaybı riski taşıyanlar (kolon silme, tip daraltma, tablo silme) varsayılan olarak
///     script'e GİRMEZ; <see cref="TableScriptOptions.AllowDataLoss"/> ile açıkça istenmelidir.
///     Girmeseler bile <see cref="TableScriptResult.DataLossActions"/> içinde ismen raporlanır —
///     sessizce düşmezler.
///
/// Index ve constraint (PK/UNIQUE/CHECK/FK) değişiklikleri de üretilir: aynı ad + farklı
/// tanım = drop + recreate. FK'ler tabloya özgü sırada yazılır; başka bir tabloyu referans
/// eden yeni bir FK ile o tablonun aynı script'teki değişimi çakışırsa çapraz sıra elle
/// kontrol edilmelidir.
///
/// KAPSAM SINIRI: kolon SIRASI değişimi (ortaya kolon ekleme → tablo yeniden oluşturma),
/// IDENTITY/computed kolon değişimi ve veri taşıma ÜRETİLMEZ; atlanır ve bildirilir.
/// </summary>
public static class TableScriptGenerator
{
    public static TableScriptResult Generate(
        CompareResult result, ISet<ObjectKey>? selection = null, TableScriptOptions? options = null)
    {
        options ??= TableScriptOptions.Default;
        var comparer = ObjectKeyComparer.CaseInsensitive;

        var included = new List<ObjectKey>();
        var skipped = new List<SkippedObject>();
        var gated = new List<GatedAction>();

        var creates = new List<ObjectKey>();
        var drops = new List<ObjectKey>();
        var changes = new List<ObjectKey>();

        foreach (var diff in result.Differences)
        {
            if (diff.Key.Kind != ObjectKind.Table) continue;
            if (selection is not null && !selection.Contains(diff.Key)) continue;

            switch (diff.Kind)
            {
                case DiffKind.Indeterminate:
                    skipped.Add(new SkippedObject(diff.Key, "karşılaştırılamadı — tanımı okunamıyor"));
                    break;
                case DiffKind.Added: creates.Add(diff.Key); break;
                case DiffKind.Removed: drops.Add(diff.Key); break;
                case DiffKind.Changed: changes.Add(diff.Key); break;
            }
        }

        var orderedCreates = TopologicalOrder(creates, result.Source, comparer, out var hadCycle);

        var sb = new StringBuilder(8192);
        WriteHeader(sb, result, options, hadCycle);

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

        // FK'ler tablo değişikliklerinden AYRI toplanır: tüm FK drop'ları en başta, tüm FK
        // add'leri en sonda çalışır. Böylece bir tablonun yeni FK'si başka bir tablonun aynı
        // script'teki değişimini beklemek zorunda kalmaz (çapraz tablo sıra sorunu çözülür).
        var fkDrops = new List<string>();
        var fkAdds = new List<string>();
        var body = new StringBuilder(4096);

        foreach (var key in orderedCreates)
            EmitCreate(body, key, result, included, skipped);

        foreach (var key in changes.OrderBy(k => k.Schema, StringComparer.OrdinalIgnoreCase)
                                    .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            EmitAlter(body, key, result, options, included, skipped, gated, fkDrops, fkAdds);

        // Tablo silme en sona: değişen tablolar ona bağlı FK'ler önce düşmüş olur.
        foreach (var key in drops.OrderBy(k => k.Schema, StringComparer.OrdinalIgnoreCase)
                                   .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            EmitDrop(body, key, result, options, included, gated);

        // FK drop'ları EN BAŞTA (kolon/index değişikliklerini engellemesinler).
        if (fkDrops.Count > 0)
        {
            sb.AppendLine("PRINT N'Foreign key''ler düşürülüyor';");
            sb.AppendLine("GO");
            foreach (var s in fkDrops) sb.AppendLine(s);
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        sb.Append(body);

        // FK add'leri EN SONDA (referans edilen tablolar artık var).
        if (fkAdds.Count > 0)
        {
            sb.AppendLine("PRINT N'Foreign key''ler ekleniyor';");
            sb.AppendLine("GO");
            foreach (var s in fkAdds) sb.AppendLine(s);
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        if (options.WrapInTransaction)
        {
            sb.AppendLine("COMMIT TRANSACTION;");
            sb.AppendLine("GO");
            sb.AppendLine();
        }

        sb.AppendLine($"PRINT N'Tamamlandı: {included.Count} tablo uygulandı.';");
        sb.AppendLine("GO");

        return new TableScriptResult(sb.ToString(), included, skipped, gated, hadCycle);
    }

    // --- CREATE ---

    private static void EmitCreate(
        StringBuilder sb, ObjectKey key, CompareResult result,
        List<ObjectKey> included, List<SkippedObject> skipped)
    {
        if (!result.Source.Objects.TryGetValue(key, out var snapshot) ||
            string.IsNullOrWhiteSpace(snapshot.DisplayScript))
        {
            skipped.Add(new SkippedObject(key, "CREATE metni yok — snapshot display script'i olmadan üretilemez"));
            return;
        }

        // DisplayScript birden fazla batch içerebilir (CREATE TABLE + GO + CREATE INDEX).
        // Bu yüzden IF/BEGIN/END ile sarmalanamaz — GO bir batch ayıracıdır, blok içinde
        // geçersizdir. Script'i olduğu gibi yayınlıyoruz; tablo zaten varsa CREATE hata
        // verir ve XACT_ABORT işlemi geri alır (yeni tablo zaten var olmamalıdır).
        sb.AppendLine($"PRINT N'Oluşturuluyor: {Describe(key)}';");
        sb.AppendLine("GO");
        sb.AppendLine(snapshot.DisplayScript!.TrimEnd());
        sb.AppendLine("GO");
        sb.AppendLine();
        included.Add(key);
    }

    // --- ALTER (kolon seviyesi) ---

    private static void EmitAlter(
        StringBuilder sb, ObjectKey key, CompareResult result, TableScriptOptions options,
        List<ObjectKey> included, List<SkippedObject> skipped, List<GatedAction> gated,
        List<string> fkDrops, List<string> fkAdds)
    {
        var source = result.Source.Objects[key];
        var target = result.Target.Objects[key];

        if (source.Columns is null || target.Columns is null)
        {
            skipped.Add(new SkippedObject(key, "kolon bilgisi yok — yapısal değişiklik üretilemez"));
            return;
        }

        var diff = result.Differences.First(d => d.Key == key);
        var qualified = $"[{key.Schema}].[{key.Name}]";
        var rows = target.RowCount;

        var sourceByName = source.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var targetByName = target.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var statements = new List<string>();
        var localSkips = new List<string>();

        // ADD: kaynakta var, hedefte yok.
        foreach (var column in source.Columns)
        {
            if (targetByName.ContainsKey(column.Name)) continue;

            if (column.IsComputed)
            {
                statements.Add($"ALTER TABLE {qualified} ADD [{column.Name}] AS {ComputedFallback(column)};");
                continue;
            }

            var def = column.DefaultDefinition is not null ? $" DEFAULT {column.DefaultDefinition}" : string.Empty;
            var sql = $"ALTER TABLE {qualified} ADD [{column.Name}] {RenderType(column)} {Nullability(column)}{def};";

            // Dolu tabloya DEFAULT'suz NOT NULL kolon eklemek başarısız olur.
            if (!column.IsNullable && column.DefaultDefinition is null && rows is > 0)
            {
                gated.Add(new GatedAction(key, column.Name,
                    $"NOT NULL kolon ekleniyor, DEFAULT yok, tabloda {rows} satır var — çalışmadan önce backfill gerekir", sql));
                continue;
            }

            statements.Add(sql);
        }

        // DROP: hedefte var, kaynakta yok → veri kaybı.
        foreach (var column in target.Columns)
        {
            if (sourceByName.ContainsKey(column.Name)) continue;

            var sql = $"ALTER TABLE {qualified} DROP COLUMN [{column.Name}];";
            var action = new GatedAction(key, column.Name,
                $"Kolon siliniyor ({column.TypeDisplay}) — içindeki veri kaybolur", sql);

            if (options.AllowDataLoss) statements.Add(sql);
            else gated.Add(action);
        }

        // ALTER: iki tarafta da var ama farklı.
        foreach (var column in source.Columns)
        {
            if (!targetByName.TryGetValue(column.Name, out var current)) continue;
            if (ColumnEquivalent(column, current)) continue;

            if (column.IsIdentity != current.IsIdentity || column.IsComputed || current.IsComputed)
            {
                localSkips.Add($"[{column.Name}] — IDENTITY/computed değişimi ALTER COLUMN ile yapılamaz, tablo yeniden oluşturulmalı");
                continue;
            }

            var sql = $"ALTER TABLE {qualified} ALTER COLUMN [{column.Name}] {RenderType(column)} {Nullability(column)};";
            var risk = ClassifyAlter(column, current, rows);

            switch (risk)
            {
                case AlterRisk.Safe:
                    statements.Add(sql);
                    break;
                case AlterRisk.BlockedIfNotEmpty:
                    gated.Add(new GatedAction(key, column.Name,
                        $"{current.TypeDisplay} {(current.IsNullable ? "NULL" : "NOT NULL")} → " +
                        $"{column.TypeDisplay} {(column.IsNullable ? "NULL" : "NOT NULL")} — dolu tabloda ({rows} satır) başarısız olabilir", sql));
                    break;
                case AlterRisk.DataLoss:
                    var action = new GatedAction(key, column.Name,
                        $"{current.TypeDisplay} → {column.TypeDisplay} — daraltma, veri kırpılabilir", sql);
                    if (options.AllowDataLoss) statements.Add(sql);
                    else gated.Add(action);
                    break;
            }
        }

        // Index ve constraint değişiklikleri: drop'lar kolon değişikliğinden ÖNCE
        // (kolonu kilitleyen index önce düşmeli), add'ler SONRA.
        var pre = new List<string>();
        var post = new List<string>();
        AppendIndexConstraintDiff(qualified, source, target, pre, post, fkDrops, fkAdds, options.ValidateNewConstraints);
        AppendDefaultDiff(qualified, sourceByName, targetByName, pre, post);

        foreach (var skip in localSkips)
            skipped.Add(new SkippedObject(key, skip));

        var ordered = pre.Concat(statements).Concat(post).ToList();

        if (ordered.Count == 0)
        {
            if (localSkips.Count == 0)
            {
                skipped.Add(IsColumnOrderOnly(source.Columns, target.Columns)
                    ? new SkippedObject(key,
                        "yalnızca kolon SIRASI farklı — SQL Server sırayı ALTER ile değiştiremez; " +
                        "tablo yeniden oluşturma (veri taşıma) gerekir, ya da sıra önemsizse " +
                        "--ignore-column-order / IgnoreColumnOrder açın")
                    : new SkippedObject(key,
                        $"kolon dışı değişiklik (değişen bölümler: {string.Join(", ", diff.ChangedParts)}) — elle gözden geçirin"));
            }
            return;
        }

        sb.AppendLine($"PRINT N'Değiştiriliyor: {Describe(key)}';");
        sb.AppendLine("GO");
        foreach (var statement in ordered) sb.AppendLine(statement);
        sb.AppendLine("GO");
        sb.AppendLine();
        included.Add(key);
    }

    // --- index ve constraint diff'i ---

    /// <summary>
    /// Değişen tablonun index ve constraint farklarını üretir. Aynı isim + farklı tanım =
    /// drop + recreate. Sıra: FK drop → check drop → index/PK-UQ drop (pre); sonra kolon
    /// değişiklikleri; sonra index/PK-UQ add → check add → FK add (post). FK'lerin referans
    /// ettiği tablo aynı script'te değişiyorsa çapraz sıra elle kontrol edilmelidir.
    /// </summary>
    private static void AppendIndexConstraintDiff(
        string qualified, ObjectSnapshot source, ObjectSnapshot target,
        List<string> pre, List<string> post, List<string> fkDrops, List<string> fkAdds,
        bool validateConstraints)
    {
        // WITH CHECK: mevcut veri doğrulanır (SSDT varsayılanı). WITH NOCHECK: doğrulama
        // atlanır — dolu tabloya constraint eklenebilir ama "not trusted" olur.
        var checkClause = validateConstraints ? "WITH CHECK" : "WITH NOCHECK";
        // NOT: record eşitliği liste üyelerini referansla karşılaştırır, değerle değil —
        // bu yüzden yapısal İMZA üzerinden karşılaştırıyoruz. Aksi hâlde özdeş bir PK bile
        // "değişti" sanılıp gereksiz DROP+ADD üretilir (FK referansı varsa deploy patlar).

        // Foreign key'ler GLOBAL listelere gider: tüm FK drop'ları en başta, add'leri en sonda
        // (çapraz tablo sırası). Buraya değil, çağırana (Generate) toplanır.
        var srcFk = ByName(source.ForeignKeyDefinitions, f => f.Name);
        var tgtFk = ByName(target.ForeignKeyDefinitions, f => f.Name);
        foreach (var (name, fk) in tgtFk)
            if (!srcFk.TryGetValue(name, out var s) || Sig(s) != Sig(fk))
                fkDrops.Add($"ALTER TABLE {qualified} DROP CONSTRAINT [{name}];");
        foreach (var (name, fk) in srcFk)
            if (!tgtFk.TryGetValue(name, out var t) || Sig(t) != Sig(fk))
                fkAdds.Add(AddForeignKey(qualified, fk, checkClause));

        // Check constraint'ler.
        var srcChk = ByName(source.CheckDefinitions, c => c.Name);
        var tgtChk = ByName(target.CheckDefinitions, c => c.Name);
        foreach (var (name, chk) in tgtChk)
            if (!srcChk.TryGetValue(name, out var s) || s.Definition != chk.Definition)
                pre.Add($"ALTER TABLE {qualified} DROP CONSTRAINT [{name}];");
        var chkAdds = new List<string>();
        foreach (var (name, chk) in srcChk)
            if (!tgtChk.TryGetValue(name, out var t) || t.Definition != chk.Definition)
                chkAdds.Add($"ALTER TABLE {qualified} {checkClause} ADD CONSTRAINT [{chk.Name}] CHECK {chk.Definition};");

        // Index'ler (PK/UQ dahil).
        var srcIdx = ByName(source.IndexDefinitions, i => i.Name);
        var tgtIdx = ByName(target.IndexDefinitions, i => i.Name);
        foreach (var (name, idx) in tgtIdx)
            if (!srcIdx.TryGetValue(name, out var s) || Sig(s) != Sig(idx))
                pre.Add(DropIndex(qualified, idx));
        var idxAdds = new List<string>();
        foreach (var (name, idx) in srcIdx)
            if (!tgtIdx.TryGetValue(name, out var t) || Sig(t) != Sig(idx))
                idxAdds.Add(CreateIndex(qualified, idx));

        // Tablo içi add sırası: index/PK-UQ → check. (FK'ler global, en sonda.)
        post.AddRange(idxAdds);
        post.AddRange(chkAdds);
    }

    /// <summary>
    /// Var olan kolonlarda DEFAULT constraint değişimi: eski default drop, yeni default add.
    /// Yeni kolonların default'u zaten ADD COLUMN içinde satır içi verilir; burada yalnızca
    /// iki tarafta da olan kolonların default farkı ele alınır.
    /// </summary>
    private static void AppendDefaultDiff(
        string qualified,
        Dictionary<string, ColumnInfo> sourceByName,
        Dictionary<string, ColumnInfo> targetByName,
        List<string> pre, List<string> post)
    {
        foreach (var (name, source) in sourceByName)
        {
            if (!targetByName.TryGetValue(name, out var target)) continue; // yeni kolon → inline
            if (string.Equals(source.DefaultDefinition, target.DefaultDefinition, StringComparison.Ordinal)) continue;

            // Eski default'u düşür (gerçek adıyla — sistem üretimi olsa bile katalogdan geldi).
            if (target.DefaultDefinition is not null && target.DefaultName is not null)
                pre.Add($"ALTER TABLE {qualified} DROP CONSTRAINT [{target.DefaultName}];");

            // Yeni default'u ekle. Kullanıcı adı varsa koru; sistem üretimiyse adsız bırak.
            if (source.DefaultDefinition is not null)
            {
                var named = source.DefaultName is not null && !source.DefaultIsSystemNamed;
                var namePart = named ? $"CONSTRAINT [{source.DefaultName}] " : string.Empty;
                post.Add($"ALTER TABLE {qualified} ADD {namePart}DEFAULT {source.DefaultDefinition} FOR [{name}];");
            }
        }
    }

    private static string DropIndex(string qualified, IndexDefinition idx) => idx.IsConstraint
        ? $"ALTER TABLE {qualified} DROP CONSTRAINT [{idx.Name}];"
        : $"DROP INDEX [{idx.Name}] ON {qualified};";

    private static string CreateIndex(string qualified, IndexDefinition idx)
    {
        if (idx.IsConstraint)
        {
            var kind = idx.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE";
            return $"ALTER TABLE {qualified} ADD CONSTRAINT [{idx.Name}] {kind} {Clustered(idx.TypeDesc)} ({KeyList(idx, ordered: true)});";
        }

        var columnstore = idx.TypeDesc.Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase);
        var unique = idx.IsUnique && !columnstore ? "UNIQUE " : string.Empty;
        var sb = new StringBuilder($"CREATE {unique}{idx.TypeDesc} INDEX [{idx.Name}] ON {qualified} ({KeyList(idx, ordered: !columnstore)})");
        if (idx.IncludedColumns.Count > 0)
            sb.Append(" INCLUDE (").Append(string.Join(", ", idx.IncludedColumns.Select(c => $"[{c}]"))).Append(')');
        if (idx.FilterDefinition is not null)
            sb.Append(" WHERE ").Append(idx.FilterDefinition);
        sb.Append(';');
        return sb.ToString();
    }

    private static string AddForeignKey(string qualified, ForeignKeyDefinition fk, string checkClause)
    {
        var parent = string.Join(", ", fk.Columns.Select(c => $"[{c.Parent}]"));
        var referenced = string.Join(", ", fk.Columns.Select(c => $"[{c.Referenced}]"));
        var sb = new StringBuilder(
            $"ALTER TABLE {qualified} {checkClause} ADD CONSTRAINT [{fk.Name}] FOREIGN KEY ({parent}) " +
            $"REFERENCES [{fk.ReferencedSchema}].[{fk.ReferencedName}] ({referenced})");
        if (fk.DeleteAction != 0) sb.Append(" ON DELETE ").Append(ReferentialAction(fk.DeleteAction));
        if (fk.UpdateAction != 0) sb.Append(" ON UPDATE ").Append(ReferentialAction(fk.UpdateAction));
        sb.Append(';');
        return sb.ToString();
    }

    private static string KeyList(IndexDefinition idx, bool ordered) => string.Join(", ",
        idx.KeyColumns.Select(k => ordered ? $"[{k.Column}] {(k.Descending ? "DESC" : "ASC")}" : $"[{k.Column}]"));

    private static string Clustered(string typeDesc) =>
        typeDesc.Contains("CLUSTERED", StringComparison.OrdinalIgnoreCase)
        && !typeDesc.StartsWith("NON", StringComparison.OrdinalIgnoreCase)
            ? "CLUSTERED" : "NONCLUSTERED";

    private static string ReferentialAction(byte action) => action switch
    {
        1 => "CASCADE",
        2 => "SET NULL",
        3 => "SET DEFAULT",
        _ => "NO ACTION",
    };

    private static Dictionary<string, T> ByName<T>(IReadOnlyList<T>? items, Func<T, string> name) =>
        (items ?? []).ToDictionary(name, StringComparer.OrdinalIgnoreCase);

    // Yapısal imzalar: liste üyeleri değerle karşılaştırılsın diye.
    private static string Sig(IndexDefinition i) =>
        $"pk={i.IsPrimaryKey}|uq={i.IsUniqueConstraint}|u={i.IsUnique}|t={i.TypeDesc}|" +
        $"keys={string.Join(",", i.KeyColumns.Select(k => $"{k.Column}:{(k.Descending ? "D" : "A")}"))}|" +
        $"inc={string.Join(",", i.IncludedColumns)}|f={i.FilterDefinition ?? ""}";

    private static string Sig(ForeignKeyDefinition f) =>
        $"ref={f.ReferencedSchema}.{f.ReferencedName}|" +
        $"cols={string.Join(",", f.Columns.Select(c => $"{c.Parent}>{c.Referenced}"))}|" +
        $"del={f.DeleteAction}|upd={f.UpdateAction}";

    // --- DROP ---

    private static void EmitDrop(
        StringBuilder sb, ObjectKey key, CompareResult result, TableScriptOptions options,
        List<ObjectKey> included, List<GatedAction> gated)
    {
        var rows = result.Target.Objects.TryGetValue(key, out var t) ? t.RowCount : null;
        var sql = $"DROP TABLE [{key.Schema}].[{key.Name}];";
        var action = new GatedAction(key, "(tablo)",
            $"Tablo siliniyor{(rows is > 0 ? $" — {rows} satır kaybolur" : string.Empty)}", sql);

        if (!options.IncludeDrops || !options.AllowDataLoss)
        {
            gated.Add(action);
            return;
        }

        sb.AppendLine($"PRINT N'Siliniyor: {Describe(key)}';");
        sb.AppendLine("GO");
        sb.AppendLine($"IF OBJECT_ID(N'[{key.Schema}].[{key.Name}]', N'U') IS NOT NULL");
        sb.AppendLine($"    {sql}");
        sb.AppendLine("GO");
        sb.AppendLine();
        included.Add(key);
    }

    // --- risk sınıflandırma ---

    private enum AlterRisk { Safe, BlockedIfNotEmpty, DataLoss }

    private static AlterRisk ClassifyAlter(ColumnInfo source, ColumnInfo target, long? rows)
    {
        // NULL → NOT NULL: mevcut satırlarda NULL varsa patlar.
        if (!source.IsNullable && target.IsNullable && rows is > 0)
            return AlterRisk.BlockedIfNotEmpty;

        var sameType = string.Equals(source.TypeName, target.TypeName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(source.TypeSchema, target.TypeSchema, StringComparison.OrdinalIgnoreCase);

        if (sameType)
        {
            if (target.MaxLength == -1 && source.MaxLength != -1) return AlterRisk.DataLoss; // MAX → sabit
            if (source.MaxLength == -1) return AlterRisk.Safe;                                // → MAX
            if (source.MaxLength != target.MaxLength)
                return source.MaxLength > target.MaxLength ? AlterRisk.Safe : AlterRisk.DataLoss;
            if (source.Precision != target.Precision || source.Scale != target.Scale)
                return source.Precision >= target.Precision && source.Scale >= target.Scale
                    ? AlterRisk.Safe : AlterRisk.DataLoss;
            return AlterRisk.Safe; // yalnızca nullability/collation farkı
        }

        // Farklı tip: emin olunmayan her tip değişimi veri kaybı sayılır.
        return AlterRisk.DataLoss;
    }

    /// <summary>
    /// İki tablonun kolon KÜMESİ ve her kolonun tanımı aynı ama SIRASI farklı mı?
    /// Öyleyse tek fark kolon sırasıdır — SQL Server bunu ALTER ile değiştiremez.
    /// </summary>
    private static bool IsColumnOrderOnly(IReadOnlyList<ColumnInfo> source, IReadOnlyList<ColumnInfo> target)
    {
        if (source.Count != target.Count) return false;

        var byName = target.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var c in source)
        {
            if (!byName.TryGetValue(c.Name, out var other)) return false;
            if (!ColumnEquivalent(c, other)) return false;
            if (!string.Equals(c.DefaultDefinition, other.DefaultDefinition, StringComparison.Ordinal)) return false;
        }

        for (var i = 0; i < source.Count; i++)
            if (!string.Equals(source[i].Name, target[i].Name, StringComparison.OrdinalIgnoreCase))
                return true; // aynı küme, farklı sıra

        return false;
    }

    /// <summary>Yalnızca üretebildiğimiz alanlarda eşitlik: tip, uzunluk, hassasiyet, nullability, collation.</summary>
    private static bool ColumnEquivalent(ColumnInfo a, ColumnInfo b) =>
        string.Equals(a.TypeName, b.TypeName, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.TypeSchema, b.TypeSchema, StringComparison.OrdinalIgnoreCase) &&
        a.MaxLength == b.MaxLength && a.Precision == b.Precision && a.Scale == b.Scale &&
        a.IsNullable == b.IsNullable && a.IsIdentity == b.IsIdentity && a.IsComputed == b.IsComputed &&
        string.Equals(a.Collation, b.Collation, StringComparison.OrdinalIgnoreCase);

    // --- tip yazımı ---

    private static string RenderType(ColumnInfo c)
    {
        if (!string.Equals(c.TypeSchema, "sys", StringComparison.OrdinalIgnoreCase))
            return $"[{c.TypeSchema}].[{c.TypeName}]";

        var name = c.TypeName.ToLowerInvariant();
        var len = c.MaxLength.ToString(CultureInfo.InvariantCulture);

        var rendered = name switch
        {
            "varchar" or "char" or "varbinary" or "binary" =>
                c.MaxLength == -1 ? $"{name}(max)" : $"{name}({len})",
            "nvarchar" or "nchar" =>
                c.MaxLength == -1 ? $"{name}(max)" : $"{name}({c.MaxLength / 2})",
            "decimal" or "numeric" => $"{name}({c.Precision},{c.Scale})",
            "datetime2" or "time" or "datetimeoffset" => $"{name}({c.Scale})",
            "float" => c.Precision == 53 ? "float" : $"float({c.Precision})",
            _ => name,
        };

        // COLLATE yalnızca karakter tiplerinde anlamlı.
        if (c.Collation is not null && name is "varchar" or "char" or "nvarchar" or "nchar" or "text" or "ntext")
            rendered += $" COLLATE {c.Collation}";

        return rendered;
    }

    private static string Nullability(ColumnInfo c) => c.IsNullable ? "NULL" : "NOT NULL";

    private static string ComputedFallback(ColumnInfo c) =>
        c.DefaultDefinition ?? "/* computed ifadesi snapshot'ta yok — elle doldurun */ NULL";

    // --- sıralama (yeni tablolar) ---

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

        foreach (var key in nodes.OrderBy(k => k.Schema, StringComparer.OrdinalIgnoreCase)
                                  .ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase))
            Visit(key);

        hadCycle = cycle;
        return ordered;
    }

    // --- başlık ve yardımcılar ---

    private static void WriteHeader(
        StringBuilder sb, CompareResult result, TableScriptOptions options, bool hadCycle)
    {
        sb.AppendLine("/* ---- 2) Tablolar --------------------------------------------------------");
        sb.AppendLine("   Yeni tablo CREATE · kolon ADD/ALTER/DROP · index & constraint (PK/UNIQUE/");
        sb.AppendLine("   CHECK/FK). Kolon sırası değişimi (tablo yeniden oluşturma) ve veri taşıma");
        sb.AppendLine("   üretilmez.");
        sb.AppendLine(options.AllowDataLoss
            ? "   Veri kaybı adımları (kolon/tablo silme, tip daraltma) DAHİL."
            : "   Veri kaybı adımları ALINMADI; sonda ayrıca listelenir.");
        if (hadCycle)
            sb.AppendLine("   !! Yeni tablolar arasında döngüsel referans var; sıralama garanti edilemedi.");
        sb.AppendLine("   ------------------------------------------------------------------------ */");
        sb.AppendLine();
    }

    private static string Describe(ObjectKey key) => $"Table [{key.Schema}].[{key.Name.Replace("'", "''")}]";
}
