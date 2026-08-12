using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SchemaDiff.Core.Hashing;
using SchemaDiff.Core.Model;
using SchemaDiff.Core.Normalization;

namespace SchemaDiff.Core.Extraction;

public sealed record SnapshotOptions
{
    public NormalizationOptions Normalization { get; init; } = NormalizationOptions.Default;

    /// <summary>
    /// Sistem tarafından üretilen constraint adları (PK__Tbl__A1B2C3, DF__Tbl__Col__X)
    /// ortamlar arasında farklıdır. Varsayılan: karşılaştırmaya dahil etme.
    /// </summary>
    public bool IgnoreSystemNamedConstraints { get; init; } = true;

    /// <summary>Index fiziksel ayarlarının tümünü (fill factor, padding, ignore_dup_key) yok say.</summary>
    public bool IgnoreIndexPhysicalOptions { get; init; }

    /// <summary>Yalnızca index fill factor'ı yok say (SSDT: "Ignore fill factor").</summary>
    public bool IgnoreFillFactor { get; init; }

    /// <summary>Yalnızca index padding'i yok say (SSDT: "Ignore index padding").</summary>
    public bool IgnoreIndexPadding { get; init; }

    /// <summary>SET ANSI_NULLS farkını yok say (SSDT: "Ignore ANSI NULLS").</summary>
    public bool IgnoreAnsiNulls { get; init; }

    /// <summary>SET QUOTED_IDENTIFIER farkını yok say (SSDT: "Ignore quoted identifiers").</summary>
    public bool IgnoreQuotedIdentifiers { get; init; }

    /// <summary>IDENTITY increment (artış) değerini yok say (SSDT: "Ignore increment").</summary>
    public bool IgnoreIdentityIncrement { get; init; }

    /// <summary>DML trigger'ın etkin/pasif durumunu yok say (SSDT: "Ignore DML trigger state").</summary>
    public bool IgnoreDmlTriggerState { get; init; }

    /// <summary>Kolonları tanım sırasına göre değil ada göre karşılaştır.</summary>
    public bool IgnoreColumnOrder { get; init; }

    /// <summary>
    /// Kolon collation farklarını yok say. Sunucu varsayılanları farklı olan
    /// ortamlarda (örn. Turkish_CI_AS ↔ SQL_Latin1_General_CP1_CI_AS) her metin
    /// kolonu fark olarak görünür; bu gürültüyü bastırır.
    /// </summary>
    public bool IgnoreCollation { get; init; }

    /// <summary>IDENTITY seed (başlangıç) değerini yok say. Increment için ayrı seçenek var;
    /// kolonun identity olup olmadığı her hâlde karşılaştırılır.</summary>
    public bool IgnoreIdentitySeed { get; init; }

    public bool CaseSensitiveNames { get; init; }

    /// <summary>
    /// Extended property'leri (MS_Description vb.) karşılaştırma dışında bırak. Varsayılan
    /// kapalı: dokümantasyon ağırlıklı EDW'lerde bunlar gerçek şema farkıdır ve SSDT de
    /// karşılaştırır. Gürültü yaratıyorsa açılabilir.
    /// </summary>
    public bool IgnoreExtendedProperties { get; init; }

    /// <summary>
    /// Güvenliği (kullanıcı tanımlı roller, rol üyelikleri, obje/şema izinleri) karşılaştırma
    /// dışında bırak. Varsayılan kapalı — SSDT de karşılaştırır. Kullanıcılar ortama özgü
    /// olduğundan zaten kapsam dışıdır; yalnızca dolaylı olarak grantee/üye adlarında görünürler.
    /// </summary>
    public bool IgnorePermissions { get; init; }

    /// <summary>
    /// Objelerin tam metnini de üret ve sakla (modüllerde özgün gövde, tablolarda
    /// CREATE script'i). Karşılaştırma için gereksiz — yalnızca arayüzde okunabilir
    /// diff göstermek için. Bellek maliyeti şema boyutuyla orantılı, varsayılan kapalı.
    /// </summary>
    public bool KeepDisplayScripts { get; init; }

    public static readonly SnapshotOptions Default = new();
}

/// <summary>
/// Ham katalog satırlarını kanonik metne ve hash'e dönüştürür.
/// Kanonik metin deterministik olmalı: aynı şema → byte-byte aynı çıktı,
/// aksi hâlde hash karşılaştırması yalancı fark üretir.
/// </summary>
internal static class SnapshotBuilder
{
    /// <summary>Veritabanı seviyesi EP/izinleri taşıyan sentetik obje. Sabit ad — db adı değil.</summary>
    private static readonly ObjectKey DatabaseKey = new(string.Empty, "(database)", ObjectKind.Database);

    public static DatabaseSnapshot Build(CatalogSet catalog, ExtractionReport report, SnapshotOptions options)
    {
        var sw = Stopwatch.StartNew();

        var keyById = new Dictionary<int, ObjectKey>(catalog.Objects.Count);
        foreach (var o in catalog.Objects)
            keyById[o.ObjectId] = new ObjectKey(o.SchemaName, o.Name, ObjectKindMap.FromSysType(o.Type));

        // (objectId, columnId) → kolon adı. Index ve FK kanonikleştirmesi buna dayanıyor.
        var columnNames = new Dictionary<long, string>(catalog.Columns.Count);
        foreach (var c in catalog.Columns) columnNames[Pair(c.ObjectId, c.ColumnId)] = c.Name;

        var columnsBy = GroupBy(catalog.Columns, c => c.ObjectId);
        var indexesBy = GroupBy(catalog.Indexes, i => i.ObjectId);
        var indexColumnsBy = GroupBy(catalog.IndexColumns, ic => Pair(ic.ObjectId, ic.IndexId));
        var checksBy = GroupBy(catalog.CheckConstraints, c => c.ParentObjectId);
        var foreignKeysBy = GroupBy(catalog.ForeignKeys, f => f.ParentObjectId);
        var fkColumnsBy = GroupBy(catalog.ForeignKeyColumns, f => f.ConstraintObjectId);

        var keyConstraintByIndex = new Dictionary<long, KeyConstraintRow>();
        foreach (var kc in catalog.KeyConstraints)
            if (kc.UniqueIndexId is { } indexId)
                keyConstraintByIndex[Pair(kc.ParentObjectId, indexId)] = kc;

        var modulesById = catalog.Modules.ToDictionary(m => m.ObjectId);
        var synonymsById = catalog.Synonyms.ToDictionary(s => s.ObjectId);
        var sequencesById = catalog.Sequences.ToDictionary(s => s.ObjectId);
        var triggersById = catalog.Triggers.ToDictionary(t => t.ObjectId);
        var rowCountsById = catalog.RowCounts.ToDictionary(r => r.ObjectId, r => r.Rows);
        var temporalById = catalog.Temporal.ToDictionary(t => t.ObjectId);

        // En pahalı adım: T-SQL tokenize etme. Objeler birbirinden bağımsız, paralel koşar.
        var normalizedBodies = new ConcurrentDictionary<int, string>();
        Parallel.ForEach(catalog.Modules, module =>
        {
            if (module.Definition is null) return;
            normalizedBodies[module.ObjectId] =
                TSqlNormalizer.Normalize(module.Definition, module.UsesQuotedIdentifier, options.Normalization);
        });

        var scriptSources = new Scripting.TableScriptSources
        {
            ColumnsBy = columnsBy,
            IndexesBy = indexesBy,
            IndexColumnsBy = indexColumnsBy,
            KeyConstraintByIndex = keyConstraintByIndex,
            ChecksBy = checksBy,
            ForeignKeysBy = foreignKeysBy,
            FkColumnsBy = fkColumnsBy,
            ColumnNames = columnNames,
            KeyById = keyById,
        };

        var comparer = options.CaseSensitiveNames
            ? ObjectKeyComparer.CaseSensitive
            : ObjectKeyComparer.CaseInsensitive;
        var objects = new Dictionary<ObjectKey, ObjectSnapshot>(catalog.Objects.Count + catalog.Schemas.Count, comparer);

        // Extended property'leri host objesine göre grupla; her host'a tek bir kanonik parça.
        var extendedByHost = options.IgnoreExtendedProperties
            ? new Dictionary<ObjectKey, string>(comparer)
            : BuildExtendedProperties(catalog, keyById, columnNames, comparer);

        // İzinleri host objesine göre grupla (obje/kolon → obje, şema → şema).
        var permissionsByHost = options.IgnorePermissions
            ? new Dictionary<ObjectKey, string>(comparer)
            : BuildPermissions(catalog, keyById, columnNames, comparer);

        // Sentetik "(database)" objesi: veritabanı seviyesi (class 0) EP ve izinleri taşır.
        // Sabit adla anahtarlanır — db adı ortamlar arası değişir, aksi hâlde Add/Remove görünürdü.
        // Her iki tarafta da hep var olsun ki fark "Changed" görünsün (Add/Remove değil).
        var dbSnapshot = new ObjectSnapshot { Key = DatabaseKey, Hash = UInt128.Zero };
        SetPart(dbSnapshot, "definition", "database");
        SetPart(dbSnapshot, "extendedProperties", extendedByHost.GetValueOrDefault(DatabaseKey, string.Empty));
        SetPart(dbSnapshot, "permissions", permissionsByHost.GetValueOrDefault(DatabaseKey, string.Empty));
        Finalize(dbSnapshot);
        objects[DatabaseKey] = dbSnapshot;

        foreach (var schema in catalog.Schemas)
        {
            var key = new ObjectKey(schema.Name, schema.Name, ObjectKind.Schema);
            var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero };
            SetPart(snapshot, "definition", $"schema|{schema.Name}");
            // Detay panelinde ham "schema|RPT" yerine okunur script göster (tablo gibi).
            if (options.KeepDisplayScripts) snapshot.DisplayScript = $"CREATE SCHEMA [{schema.Name}];";
            SetPart(snapshot, "extendedProperties", extendedByHost.GetValueOrDefault(key, string.Empty));
            SetPart(snapshot, "permissions", permissionsByHost.GetValueOrDefault(key, string.Empty));
            Finalize(snapshot);
            objects[key] = snapshot;
        }

        // Kullanıcı tanımlı roller: üst seviye objeler (şemalar gibi). Owner ve üye listesi.
        if (!options.IgnorePermissions)
        {
            var membersByRole = new Dictionary<int, List<string>>();
            foreach (var m in catalog.RoleMembers)
            {
                if (!membersByRole.TryGetValue(m.RolePrincipalId, out var list))
                    membersByRole[m.RolePrincipalId] = list = [];
                if (m.MemberName is not null) list.Add(m.MemberName);
            }

            foreach (var role in catalog.Roles)
            {
                var key = new ObjectKey(string.Empty, role.Name, ObjectKind.Role);
                var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero };
                // Sabit rollerde owner değişmez — yalnızca ÜYELİK karşılaştırılır.
                if (!role.IsFixed) SetPart(snapshot, "owner", $"owner|{role.Owner ?? "-"}");

                var members = membersByRole.GetValueOrDefault(role.PrincipalId) ?? [];
                members.Sort(StringComparer.OrdinalIgnoreCase);
                if (members.Count > 0)
                    SetPart(snapshot, "members", string.Join('\n', members.Select(m => $"member|{m}")));

                // Detay panelinde ham "member|KUVEYTTURK\x" yerine okunur script göster.
                if (options.KeepDisplayScripts)
                {
                    var lines = new List<string>();
                    if (!role.IsFixed)
                        lines.Add(role.Owner is { } o && o != "-"
                            ? $"CREATE ROLE [{role.Name}] AUTHORIZATION [{o}];"
                            : $"CREATE ROLE [{role.Name}];");
                    lines.AddRange(members.Select(m => $"ALTER ROLE [{role.Name}] ADD MEMBER [{m}];"));
                    snapshot.DisplayScript = lines.Count > 0
                        ? string.Join('\n', lines)
                        : $"-- ROLE [{role.Name}] (üyesiz)";
                }

                Finalize(snapshot);
                objects[key] = snapshot;
            }

            // Veritabanı kullanıcıları: üst seviye principal'lar. Login/SID ortama özgü olduğu
            // için karşılaştırmaya girmez; ad (key) + tip + default schema kıyaslanır.
            foreach (var user in catalog.Users)
            {
                var key = new ObjectKey(string.Empty, user.Name, ObjectKind.User);
                var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero };
                SetPart(snapshot, "definition", $"user|type={user.Type}|schema={user.DefaultSchema ?? "dbo"}");
                if (options.KeepDisplayScripts) snapshot.DisplayScript = UserCreateScript(user);
                Finalize(snapshot);
                objects[key] = snapshot;
            }
        }

        // Kullanıcı tanımlı alias tipler: üst seviye objeler. Baz tip + facet'ler.
        foreach (var udt in catalog.UserDefinedTypes)
        {
            var key = new ObjectKey(udt.SchemaName, udt.Name, ObjectKind.UserDefinedType);
            var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero };
            var coll = udt.Collation is not null && !options.IgnoreCollation ? $"|coll={udt.Collation}" : string.Empty;
            SetPart(snapshot, "definition",
                $"udt|base={udt.BaseType}|len={udt.MaxLength.ToString(CultureInfo.InvariantCulture)}" +
                $"|prec={udt.Precision}|scale={udt.Scale}|null={Flag(udt.IsNullable)}{coll}");
            if (options.KeepDisplayScripts)
                snapshot.DisplayScript =
                    $"CREATE TYPE [{udt.SchemaName}].[{udt.Name}] FROM " +
                    $"{RenderFacetType(udt.BaseType, udt.MaxLength, udt.Precision, udt.Scale)} " +
                    $"{(udt.IsNullable ? "NULL" : "NOT NULL")};";
            Finalize(snapshot);
            objects[key] = snapshot;
        }

        // Table type'lar: üst seviye objeler, kolon yapısıyla karşılaştırılır (ALTER edilemez;
        // kolon farkı drop+recreate demektir, ama farkı görmek yine de değerli).
        var tableTypeColumnsBy = GroupBy(catalog.TableTypeColumns, c => c.ObjectId);
        foreach (var tt in catalog.TableTypes)
        {
            var key = new ObjectKey(tt.SchemaName, tt.Name, ObjectKind.TableType);
            var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero };
            var ttColumns = tableTypeColumnsBy.GetValueOrDefault(tt.TypeTableObjectId);
            SetPart(snapshot, "columns", BuildTableTypeColumns(ttColumns, options));
            if (options.KeepDisplayScripts)
                snapshot.DisplayScript = RenderTableType(tt, ttColumns, options);
            Finalize(snapshot);
            objects[key] = snapshot;
        }

        // Partition function'lar: üst seviye objeler. Aralık yönü + girdi tipi + sıralı sınır değerleri.
        var rangeValuesBy = GroupBy(catalog.PartitionRangeValues, v => v.FunctionId);
        foreach (var pf in catalog.PartitionFunctions)
        {
            var key = new ObjectKey(string.Empty, pf.Name, ObjectKind.PartitionFunction);
            var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero };
            var boundValues = (rangeValuesBy.GetValueOrDefault(pf.FunctionId) ?? [])
                .OrderBy(v => v.BoundaryId).Select(v => v.Value ?? "NULL").ToList();
            SetPart(snapshot, "definition",
                $"pf|type={pf.InputType ?? "-"}|range={(pf.BoundaryOnRight ? "RIGHT" : "LEFT")}" +
                $"|bounds={string.Join(',', boundValues)}");
            if (options.KeepDisplayScripts)
            {
                var literals = boundValues.Select(v => PartitionLiteral(pf.InputType, v));
                snapshot.DisplayScript =
                    $"CREATE PARTITION FUNCTION [{pf.Name}] ([{pf.InputType}]) " +
                    $"AS RANGE {(pf.BoundaryOnRight ? "RIGHT" : "LEFT")} FOR VALUES ({string.Join(", ", literals)});";
            }
            Finalize(snapshot);
            objects[key] = snapshot;
        }

        // Partition scheme'ler: bağlı olduğu function + sıralı filegroup eşlemesi.
        var schemeFilesBy = GroupBy(catalog.PartitionSchemeFiles, f => f.SchemeId);
        foreach (var ps in catalog.PartitionSchemes)
        {
            var key = new ObjectKey(string.Empty, ps.Name, ObjectKind.PartitionScheme);
            var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero };
            var fileList = (schemeFilesBy.GetValueOrDefault(ps.SchemeId) ?? [])
                .OrderBy(f => f.DestinationId).Select(f => f.Filegroup).ToList();
            SetPart(snapshot, "definition",
                $"ps|function={ps.FunctionName}|files={string.Join(',', fileList)}");
            if (options.KeepDisplayScripts)
                snapshot.DisplayScript =
                    $"CREATE PARTITION SCHEME [{ps.Name}] AS PARTITION [{ps.FunctionName}] TO (" +
                    $"{string.Join(", ", fileList.Select(f => $"[{f}]"))});";
            Finalize(snapshot);
            objects[key] = snapshot;
        }

        // Veritabanı seviyesi DDL trigger'ları (şema yok, ad benzersiz).
        foreach (var dt in catalog.DdlTriggers)
        {
            var key = new ObjectKey(string.Empty, dt.Name, ObjectKind.DdlTrigger);
            var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero };
            if (dt.Definition is null)
            {
                SetPart(snapshot, "body",
                    MarkIncomparable(snapshot, "definition NULL — şifrelenmiş obje ya da yetki eksik"));
            }
            else
            {
                SetPart(snapshot, "body",
                    TSqlNormalizer.Normalize(dt.Definition, dt.UsesQuotedIdentifier, options.Normalization));
                SetPart(snapshot, "setOptions", SetOptions(dt.UsesAnsiNulls, dt.UsesQuotedIdentifier, options));
                SetPart(snapshot, "attributes", $"disabled={Flag(dt.IsDisabled)}");
                snapshot.UsesAnsiNulls = dt.UsesAnsiNulls;
                snapshot.UsesQuotedIdentifier = dt.UsesQuotedIdentifier;
                snapshot.IsDisabled = dt.IsDisabled;
                if (options.KeepDisplayScripts) snapshot.DisplayScript = dt.Definition;
            }
            Finalize(snapshot);
            objects[key] = snapshot;
        }

        foreach (var obj in catalog.Objects)
        {
            var key = keyById[obj.ObjectId];
            if (key.Kind == ObjectKind.Unknown) continue;

            var snapshot = new ObjectSnapshot { Key = key, Hash = UInt128.Zero, ModifyDate = obj.ModifyDate };

            switch (key.Kind)
            {
                case ObjectKind.Table:
                    snapshot.Columns = ToColumnInfo(columnsBy.GetValueOrDefault(obj.ObjectId));
                    snapshot.RowCount = rowCountsById.TryGetValue(obj.ObjectId, out var rows) ? rows : null;
                    if (options.KeepDisplayScripts)
                    {
                        snapshot.DisplayScript = Scripting.TableScriptWriter.Write(key, obj.ObjectId, scriptSources, options);
                        snapshot.IndexDefinitions = BuildIndexDefinitions(
                            obj.ObjectId, indexesBy, indexColumnsBy, keyConstraintByIndex, columnNames, options);
                        snapshot.CheckDefinitions = BuildCheckDefinitions(checksBy.GetValueOrDefault(obj.ObjectId), options);
                        snapshot.ForeignKeyDefinitions = BuildForeignKeyDefinitions(
                            foreignKeysBy.GetValueOrDefault(obj.ObjectId), fkColumnsBy, columnNames, keyById, options);
                    }
                    SetPart(snapshot, "columns", BuildColumns(columnsBy.GetValueOrDefault(obj.ObjectId), options));
                    SetPart(snapshot, "indexes", BuildIndexes(obj.ObjectId, indexesBy, indexColumnsBy, keyConstraintByIndex, columnNames, options));
                    SetPart(snapshot, "checks", BuildChecks(checksBy.GetValueOrDefault(obj.ObjectId), options));
                    SetPart(snapshot, "foreignKeys", BuildForeignKeys(foreignKeysBy.GetValueOrDefault(obj.ObjectId), fkColumnsBy, columnNames, keyById, options));
                    if (temporalById.TryGetValue(obj.ObjectId, out var temporal))
                        SetPart(snapshot, "temporal", BuildTemporal(temporal));
                    break;

                case ObjectKind.View:
                    ApplyModule(snapshot, obj.ObjectId, modulesById, normalizedBodies, options);
                    SetPart(snapshot, "columns", BuildColumns(columnsBy.GetValueOrDefault(obj.ObjectId), options));
                    SetPart(snapshot, "indexes", BuildIndexes(obj.ObjectId, indexesBy, indexColumnsBy, keyConstraintByIndex, columnNames, options));
                    break;

                case ObjectKind.Trigger:
                    ApplyModule(snapshot, obj.ObjectId, modulesById, normalizedBodies, options);
                    if (triggersById.TryGetValue(obj.ObjectId, out var trigger))
                    {
                        snapshot.IsDisabled = trigger.IsDisabled;
                        if (keyById.TryGetValue(obj.ParentObjectId, out var parentKey)) snapshot.Parent = parentKey;
                        var parent = keyById.TryGetValue(obj.ParentObjectId, out var p) ? p.ToString() : $"#{obj.ParentObjectId}";
                        // "Ignore DML trigger state" açıksa etkin/pasif farkı karşılaştırmaya girmez.
                        var disabled = options.IgnoreDmlTriggerState ? string.Empty : $"|disabled={Flag(trigger.IsDisabled)}";
                        SetPart(snapshot, "attributes",
                            $"parent={parent}{disabled}|insteadOf={Flag(trigger.IsInsteadOf)}");
                    }
                    break;

                case ObjectKind.Procedure:
                case ObjectKind.ScalarFunction:
                case ObjectKind.InlineTableFunction:
                case ObjectKind.TableFunction:
                    ApplyModule(snapshot, obj.ObjectId, modulesById, normalizedBodies, options);
                    break;

                case ObjectKind.Synonym:
                    if (synonymsById.TryGetValue(obj.ObjectId, out var syn))
                    {
                        SetPart(snapshot, "definition", $"synonym|base={syn.BaseObjectName}");
                        if (options.KeepDisplayScripts)
                            snapshot.DisplayScript = $"CREATE SYNONYM [{key.Schema}].[{key.Name}] FOR {syn.BaseObjectName};";
                    }
                    else SetPart(snapshot, "definition", MarkIncomparable(snapshot, "sys.synonyms satırı bulunamadı"));
                    break;

                case ObjectKind.Sequence:
                    if (sequencesById.TryGetValue(obj.ObjectId, out var seq))
                    {
                        SetPart(snapshot, "definition", BuildSequence(seq));
                        if (options.KeepDisplayScripts) snapshot.DisplayScript = RenderSequence(key, seq);
                    }
                    else SetPart(snapshot, "definition", MarkIncomparable(snapshot, "sys.sequences satırı bulunamadı"));
                    break;
            }

            // Extended property'ler ve izinler host objenin parçalarıdır: eklenmesi/değişmesi
            // objeyi "Changed" yapar, ayrı bir obje sınıfı gerektirmez.
            SetPart(snapshot, "extendedProperties", extendedByHost.GetValueOrDefault(key, string.Empty));
            SetPart(snapshot, "permissions", permissionsByHost.GetValueOrDefault(key, string.Empty));

            Finalize(snapshot);
            objects[key] = snapshot;
        }

        report.Normalization = sw.Elapsed;

        // Referans grafiği: yalnızca her iki ucu da bildiğimiz objeler arasında.
        var references = new Dictionary<ObjectKey, List<ObjectKey>>(comparer);
        foreach (var dependency in catalog.Dependencies)
        {
            if (!keyById.TryGetValue(dependency.ReferencingId, out var from)) continue;
            if (!keyById.TryGetValue(dependency.ReferencedId, out var to)) continue;
            if (!references.TryGetValue(from, out var list)) references[from] = list = [];
            list.Add(to);
        }

        // Dış (cross-db / linked server) referanslar: deploy öncesi uyarı — bu objeler
        // ancak dış kaynak hedefte de varsa çalışır.
        if (catalog.ExternalReferences.Count > 0)
        {
            var samples = catalog.ExternalReferences.Take(5).Select(e =>
            {
                var target = string.Join(".",
                    new[] { e.Server, e.Database, e.RefSchema, e.RefEntity }.Where(s => s.Length > 0));
                return $"[{e.SourceSchema}].[{e.SourceName}] → {target}";
            });
            report.Warnings.Add(
                $"{catalog.ExternalReferences.Count} dış (cross-db/linked server) referans — bu objeler " +
                $"ancak dış kaynak hedefte de varsa deploy edilir: {string.Join("; ", samples)}" +
                (catalog.ExternalReferences.Count > 5 ? " …" : string.Empty));
        }

        return new DatabaseSnapshot
        {
            Server = catalog.ServerName,
            Database = catalog.DatabaseName,
            Objects = objects,
            Report = report,
            References = references,
            IgnoredColumnOrder = options.IgnoreColumnOrder,
            IgnoredCollation = options.IgnoreCollation,
        };
    }

    // --- Parça inşası ---

    /// <summary>
    /// SET seçenekleri kanoniği. ANSI_NULLS ve QUOTED_IDENTIFIER ayrı ayrı yok sayılabilir;
    /// ikisi de yok sayılırsa boş string döner (iki tarafta da boş → fark üretmez).
    /// </summary>
    private static string SetOptions(bool ansiNulls, bool quotedIdentifier, SnapshotOptions options)
    {
        var parts = new List<string>(2);
        if (!options.IgnoreAnsiNulls) parts.Add($"ansiNulls={Flag(ansiNulls)}");
        if (!options.IgnoreQuotedIdentifiers) parts.Add($"quotedIdentifier={Flag(quotedIdentifier)}");
        return string.Join('|', parts);
    }

    private static void ApplyModule(
        ObjectSnapshot snapshot, int objectId,
        Dictionary<int, ModuleRow> modules, ConcurrentDictionary<int, string> bodies,
        SnapshotOptions options)
    {
        if (!modules.TryGetValue(objectId, out var module))
        {
            SetPart(snapshot, "body", MarkIncomparable(snapshot, "sys.sql_modules satırı yok"));
            return;
        }

        if (module.Definition is null)
        {
            SetPart(snapshot, "body",
                MarkIncomparable(snapshot, "definition NULL — şifrelenmiş obje ya da VIEW DEFINITION yetkisi yok"));
            return;
        }

        if (options.KeepDisplayScripts) snapshot.DisplayScript = module.Definition;

        snapshot.UsesAnsiNulls = module.UsesAnsiNulls;
        snapshot.UsesQuotedIdentifier = module.UsesQuotedIdentifier;

        SetPart(snapshot, "setOptions", SetOptions(module.UsesAnsiNulls, module.UsesQuotedIdentifier, options));
        SetPart(snapshot, "body", bodies.GetValueOrDefault(objectId, string.Empty));
    }

    private static IReadOnlyList<ColumnInfo>? ToColumnInfo(List<ColumnRow>? columns) =>
        columns?.OrderBy(c => c.ColumnId)
            .Select(c => new ColumnInfo(
                c.Name, c.TypeSchema, c.TypeName, c.MaxLength, c.Precision, c.Scale,
                c.IsNullable, c.Collation, c.IsIdentity, c.IsComputed, c.DefaultDefinition,
                c.DefaultName, c.DefaultIsSystemNamed == true))
            .ToList();

    private static string BuildColumns(List<ColumnRow>? columns, SnapshotOptions options)
    {
        if (columns is null || columns.Count == 0) return string.Empty;

        var ordered = options.IgnoreColumnOrder
            ? columns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            : columns.OrderBy(c => c.ColumnId);

        var sb = new StringBuilder(columns.Count * 96);
        foreach (var c in ordered)
        {
            sb.Append("col|").Append(c.Name)
              .Append('|').Append(c.TypeSchema).Append('.').Append(c.TypeName)
              .Append("|len=").Append(c.MaxLength.ToString(CultureInfo.InvariantCulture))
              .Append("|prec=").Append(c.Precision.ToString(CultureInfo.InvariantCulture))
              .Append("|scale=").Append(c.Scale.ToString(CultureInfo.InvariantCulture))
              .Append("|null=").Append(Flag(c.IsNullable));

            if (c.Collation is not null && !options.IgnoreCollation) sb.Append("|coll=").Append(c.Collation);

            if (c.IsIdentity)
            {
                // "identity" token'ı her zaman: kolonun identity olup olmadığı daima karşılaştırılır.
                // seed ve increment ayrı ayrı yok sayılabilir.
                sb.Append("|identity");
                if (!options.IgnoreIdentitySeed) sb.Append(";seed=").Append(c.IdentitySeed);
                if (!options.IgnoreIdentityIncrement) sb.Append(";inc=").Append(c.IdentityIncrement);
            }
            if (c.IsComputed)
                sb.Append("|computed=").Append(c.ComputedDefinition)
                  .Append(";persisted=").Append(Flag(c.ComputedIsPersisted == true));

            if (c.DefaultDefinition is not null)
            {
                sb.Append("|default=");
                if (!(options.IgnoreSystemNamedConstraints && c.DefaultIsSystemNamed == true))
                    sb.Append(c.DefaultName);
                sb.Append(':').Append(c.DefaultDefinition);
            }

            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string BuildIndexes(
        int objectId,
        Dictionary<int, List<IndexRow>> indexesBy,
        Dictionary<long, List<IndexColumnRow>> indexColumnsBy,
        Dictionary<long, KeyConstraintRow> keyConstraintByIndex,
        Dictionary<long, string> columnNames,
        SnapshotOptions options)
    {
        if (!indexesBy.TryGetValue(objectId, out var indexes) || indexes.Count == 0) return string.Empty;

        var lines = new List<string>(indexes.Count);
        foreach (var index in indexes)
        {
            var pairKey = Pair(objectId, index.IndexId);
            var name = index.Name ?? "(unnamed)";

            // PK/UQ'yu taşıyan index'in adı constraint adıdır; sistem üretimi ise dışarıda bırak.
            if ((index.IsPrimaryKey || index.IsUniqueConstraint)
                && keyConstraintByIndex.TryGetValue(pairKey, out var kc))
            {
                name = options.IgnoreSystemNamedConstraints && kc.IsSystemNamed ? "(system-named)" : kc.Name;
            }

            var sb = new StringBuilder(128);
            sb.Append("idx|").Append(name)
              .Append('|').Append(index.TypeDesc)
              .Append("|unique=").Append(Flag(index.IsUnique))
              .Append("|pk=").Append(Flag(index.IsPrimaryKey))
              .Append("|uq=").Append(Flag(index.IsUniqueConstraint));

            // Tümünü kapatan IgnoreIndexPhysicalOptions dışında fill ve padding ayrı ayrı da yok sayılabilir.
            if (!options.IgnoreIndexPhysicalOptions && !options.IgnoreFillFactor)
                sb.Append("|fill=").Append(index.FillFactor.ToString(CultureInfo.InvariantCulture));
            if (!options.IgnoreIndexPhysicalOptions && !options.IgnoreIndexPadding)
                sb.Append("|padded=").Append(Flag(index.IsPadded));
            if (!options.IgnoreIndexPhysicalOptions)
                sb.Append("|ignoreDupKey=").Append(Flag(index.IgnoreDupKey));

            if (index.FilterDefinition is not null) sb.Append("|filter=").Append(index.FilterDefinition);

            var indexColumns = indexColumnsBy.GetValueOrDefault(pairKey) ?? [];

            var keys = indexColumns
                .Where(ic => !ic.IsIncluded && ic.KeyOrdinal > 0)
                .OrderBy(ic => ic.KeyOrdinal)
                .Select(ic => $"{Column(columnNames, objectId, ic.ColumnId)}{(ic.IsDescending ? " DESC" : " ASC")}");
            sb.Append("|keys=").Append(string.Join(',', keys));

            var included = indexColumns
                .Where(ic => ic.IsIncluded)
                .Select(ic => Column(columnNames, objectId, ic.ColumnId))
                .Order(StringComparer.Ordinal);
            sb.Append("|include=").Append(string.Join(',', included));

            // Columnstore index'lerde kolonların key_ordinal'ı 0 ve included değildir.
            var unordered = indexColumns
                .Where(ic => !ic.IsIncluded && ic.KeyOrdinal == 0)
                .Select(ic => Column(columnNames, objectId, ic.ColumnId))
                .Order(StringComparer.Ordinal)
                .ToList();
            if (unordered.Count > 0) sb.Append("|cols=").Append(string.Join(',', unordered));

            lines.Add(sb.ToString());
        }

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static string BuildChecks(List<CheckConstraintRow>? checks, SnapshotOptions options)
    {
        if (checks is null || checks.Count == 0) return string.Empty;

        var lines = checks.Select(c =>
        {
            var name = options.IgnoreSystemNamedConstraints && c.IsSystemNamed ? "(system-named)" : c.Name;
            return $"chk|{name}|{c.Definition}|disabled={Flag(c.IsDisabled)}|notTrusted={Flag(c.IsNotTrusted)}";
        }).ToList();

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static string BuildForeignKeys(
        List<ForeignKeyRow>? foreignKeys,
        Dictionary<int, List<ForeignKeyColumnRow>> fkColumnsBy,
        Dictionary<long, string> columnNames,
        Dictionary<int, ObjectKey> keyById,
        SnapshotOptions options)
    {
        if (foreignKeys is null || foreignKeys.Count == 0) return string.Empty;

        var lines = foreignKeys.Select(fk =>
        {
            var name = options.IgnoreSystemNamedConstraints && fk.IsSystemNamed ? "(system-named)" : fk.Name;
            var referenced = keyById.TryGetValue(fk.ReferencedObjectId, out var refKey)
                ? $"[{refKey.Schema}].[{refKey.Name}]"
                : $"#{fk.ReferencedObjectId}";

            var columns = (fkColumnsBy.GetValueOrDefault(fk.ObjectId) ?? [])
                .OrderBy(c => c.ConstraintColumnId)
                .Select(c =>
                    $"{Column(columnNames, c.ParentObjectId, c.ParentColumnId)}>" +
                    $"{Column(columnNames, c.ReferencedObjectId, c.ReferencedColumnId)}");

            return $"fk|{name}|ref={referenced}|cols={string.Join(',', columns)}" +
                   $"|onDelete={fk.DeleteAction}|onUpdate={fk.UpdateAction}" +
                   $"|disabled={Flag(fk.IsDisabled)}|notTrusted={Flag(fk.IsNotTrusted)}";
        }).ToList();

        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    /// <summary>
    /// Extended property'leri host objeye (tablo/view/prosedür/kolon → o obje; şema → şema
    /// objesi) bağlar ve her host için deterministik, ada göre sıralı bir kanonik metin üretir.
    /// Kolon property'leri host objesinin metnine "col:&lt;ad&gt;" niteleyicisiyle girer.
    /// </summary>
    private static Dictionary<ObjectKey, string> BuildExtendedProperties(
        CatalogSet catalog,
        Dictionary<int, ObjectKey> keyById,
        Dictionary<long, string> columnNames,
        ObjectKeyComparer comparer)
    {
        var schemaNameById = new Dictionary<int, string>(catalog.Schemas.Count);
        foreach (var s in catalog.Schemas) schemaNameById[s.SchemaId] = s.Name;

        var linesByHost = new Dictionary<ObjectKey, List<string>>(comparer);

        foreach (var ep in catalog.ExtendedProperties)
        {
            ObjectKey host;
            string scope;

            if (ep.Class == 0)
            {
                host = DatabaseKey;
                scope = "database";
            }
            else if (ep.Class == 3)
            {
                if (!schemaNameById.TryGetValue(ep.MajorId, out var schemaName)) continue;
                host = new ObjectKey(schemaName, schemaName, ObjectKind.Schema);
                scope = "schema";
            }
            else // class 1: obje ya da kolon
            {
                if (!keyById.TryGetValue(ep.MajorId, out var key) || key.Kind == ObjectKind.Unknown) continue;
                host = key;
                scope = ep.MinorId == 0
                    ? "obj"
                    : $"col:{columnNames.GetValueOrDefault(Pair(ep.MajorId, ep.MinorId), $"#{ep.MinorId}")}";
            }

            if (!linesByHost.TryGetValue(host, out var list)) linesByHost[host] = list = [];
            list.Add($"ep|{scope}|{ep.Name}={ep.Value ?? string.Empty}");
        }

        var result = new Dictionary<ObjectKey, string>(comparer);
        foreach (var (host, lines) in linesByHost)
        {
            lines.Sort(StringComparer.Ordinal);
            result[host] = string.Join('\n', lines);
        }
        return result;
    }

    /// <summary>
    /// İzinleri host objeye (class 1 → obje/kolon, class 3 → şema) bağlar ve her host için
    /// deterministik, sıralı kanonik metin üretir. Grantee adıyla girer (id ortama özgüdür).
    /// </summary>
    private static Dictionary<ObjectKey, string> BuildPermissions(
        CatalogSet catalog,
        Dictionary<int, ObjectKey> keyById,
        Dictionary<long, string> columnNames,
        ObjectKeyComparer comparer)
    {
        var schemaNameById = new Dictionary<int, string>(catalog.Schemas.Count);
        foreach (var s in catalog.Schemas) schemaNameById[s.SchemaId] = s.Name;

        var linesByHost = new Dictionary<ObjectKey, List<string>>(comparer);

        foreach (var p in catalog.Permissions)
        {
            ObjectKey host;
            string scope;

            if (p.Class == 0)
            {
                host = DatabaseKey;
                scope = "database";
            }
            else if (p.Class == 3)
            {
                if (!schemaNameById.TryGetValue(p.MajorId, out var schemaName)) continue;
                host = new ObjectKey(schemaName, schemaName, ObjectKind.Schema);
                scope = "schema";
            }
            else // class 1
            {
                if (!keyById.TryGetValue(p.MajorId, out var key) || key.Kind == ObjectKind.Unknown) continue;
                host = key;
                scope = p.MinorId == 0
                    ? "obj"
                    : $"col:{columnNames.GetValueOrDefault(Pair(p.MajorId, p.MinorId), $"#{p.MinorId}")}";
            }

            if (!linesByHost.TryGetValue(host, out var list)) linesByHost[host] = list = [];
            list.Add($"perm|{scope}|{p.State} {p.PermissionName} TO {p.Grantee ?? "?"}");
        }

        var result = new Dictionary<ObjectKey, string>(comparer);
        foreach (var (host, lines) in linesByHost)
        {
            lines.Sort(StringComparer.Ordinal);
            result[host] = string.Join('\n', lines);
        }
        return result;
    }

    /// <summary>Baz tip + facet'leri T-SQL tip ifadesine çevirir (varchar(20), decimal(19,4), datetime2(3)…).</summary>
    private static string RenderFacetType(string baseType, short maxLength, byte precision, byte scale)
    {
        var name = baseType.ToLowerInvariant();
        var len = maxLength.ToString(CultureInfo.InvariantCulture);
        return name switch
        {
            "varchar" or "char" or "varbinary" or "binary" => maxLength == -1 ? $"[{name}](max)" : $"[{name}]({len})",
            "nvarchar" or "nchar" => maxLength == -1 ? $"[{name}](max)" : $"[{name}]({maxLength / 2})",
            "decimal" or "numeric" => $"[{name}]({precision},{scale})",
            "datetime2" or "time" or "datetimeoffset" => $"[{name}]({scale})",
            _ => $"[{name}]",
        };
    }

    private static string RenderTableType(TableTypeRow tt, List<TableTypeColumnRow>? columns, SnapshotOptions options)
    {
        var sb = new StringBuilder(256);
        sb.Append("CREATE TYPE [").Append(tt.SchemaName).Append("].[").Append(tt.Name).AppendLine("] AS TABLE (");
        var ordered = (columns ?? []).OrderBy(c => c.ColumnId).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            sb.Append("    [").Append(c.Name).Append("] ")
              .Append(RenderFacetType(c.TypeName, c.MaxLength, c.Precision, c.Scale));
            if (c.Collation is not null && !options.IgnoreCollation) sb.Append(" COLLATE ").Append(c.Collation);
            sb.Append(c.IsNullable ? " NULL" : " NOT NULL");
            sb.AppendLine(i < ordered.Count - 1 ? "," : string.Empty);
        }
        sb.Append(");");
        return sb.ToString();
    }

    // --- Script üretimi için yapısal tanımlar ---

    private static List<IndexDefinition> BuildIndexDefinitions(
        int objectId,
        Dictionary<int, List<IndexRow>> indexesBy,
        Dictionary<long, List<IndexColumnRow>> indexColumnsBy,
        Dictionary<long, KeyConstraintRow> keyConstraintByIndex,
        Dictionary<long, string> columnNames,
        SnapshotOptions options)
    {
        var result = new List<IndexDefinition>();

        foreach (var index in indexesBy.GetValueOrDefault(objectId) ?? [])
        {
            var pairKey = Pair(objectId, index.IndexId);
            var name = index.Name;
            var systemNamed = false;

            if ((index.IsPrimaryKey || index.IsUniqueConstraint)
                && keyConstraintByIndex.TryGetValue(pairKey, out var kc))
            {
                name = kc.Name;
                systemNamed = kc.IsSystemNamed;
            }

            // Sistem üretimi PK/UQ karşılaştırma dışıysa script'e de girmez.
            if ((index.IsPrimaryKey || index.IsUniqueConstraint)
                && options.IgnoreSystemNamedConstraints && systemNamed)
                continue;
            if (string.IsNullOrEmpty(name)) continue;

            var cols = indexColumnsBy.GetValueOrDefault(pairKey) ?? [];
            var keys = cols.Where(c => !c.IsIncluded && c.KeyOrdinal > 0).OrderBy(c => c.KeyOrdinal)
                .Select(c => new IndexKeyColumn(Column(columnNames, objectId, c.ColumnId), c.IsDescending))
                .ToList();
            // Columnstore: key_ordinal 0, included değil.
            keys.AddRange(cols.Where(c => !c.IsIncluded && c.KeyOrdinal == 0)
                .Select(c => new IndexKeyColumn(Column(columnNames, objectId, c.ColumnId), false))
                .OrderBy(k => k.Column, StringComparer.Ordinal));

            var included = cols.Where(c => c.IsIncluded)
                .Select(c => Column(columnNames, objectId, c.ColumnId))
                .Order(StringComparer.Ordinal).ToList();

            result.Add(new IndexDefinition(
                name, index.IsPrimaryKey, index.IsUniqueConstraint, index.IsUnique,
                index.TypeDesc, systemNamed, keys, included, index.FilterDefinition));
        }

        return result;
    }

    private static List<CheckDefinition> BuildCheckDefinitions(List<CheckConstraintRow>? checks, SnapshotOptions options)
    {
        var result = new List<CheckDefinition>();
        foreach (var c in checks ?? [])
        {
            if (options.IgnoreSystemNamedConstraints && c.IsSystemNamed) continue;
            if (c.Definition is null) continue;
            result.Add(new CheckDefinition(c.Name, c.Definition, c.IsSystemNamed));
        }
        return result;
    }

    private static List<ForeignKeyDefinition> BuildForeignKeyDefinitions(
        List<ForeignKeyRow>? foreignKeys,
        Dictionary<int, List<ForeignKeyColumnRow>> fkColumnsBy,
        Dictionary<long, string> columnNames,
        Dictionary<int, ObjectKey> keyById,
        SnapshotOptions options)
    {
        var result = new List<ForeignKeyDefinition>();
        foreach (var fk in foreignKeys ?? [])
        {
            if (options.IgnoreSystemNamedConstraints && fk.IsSystemNamed) continue;

            var referenced = keyById.TryGetValue(fk.ReferencedObjectId, out var refKey)
                ? refKey
                : new ObjectKey(string.Empty, $"#{fk.ReferencedObjectId}", ObjectKind.Table);

            var cols = (fkColumnsBy.GetValueOrDefault(fk.ObjectId) ?? [])
                .OrderBy(c => c.ConstraintColumnId)
                .Select(c => new ForeignKeyColumnPair(
                    Column(columnNames, c.ParentObjectId, c.ParentColumnId),
                    Column(columnNames, c.ReferencedObjectId, c.ReferencedColumnId)))
                .ToList();

            result.Add(new ForeignKeyDefinition(
                fk.Name, fk.IsSystemNamed, referenced.Schema, referenced.Name, cols,
                fk.DeleteAction, fk.UpdateAction));
        }
        return result;
    }

    private static string BuildTableTypeColumns(List<TableTypeColumnRow>? columns, SnapshotOptions options)
    {
        if (columns is null || columns.Count == 0) return string.Empty;

        var ordered = options.IgnoreColumnOrder
            ? columns.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            : columns.OrderBy(c => c.ColumnId);

        var sb = new StringBuilder(columns.Count * 80);
        foreach (var c in ordered)
        {
            sb.Append("col|").Append(c.Name)
              .Append('|').Append(c.TypeSchema).Append('.').Append(c.TypeName)
              .Append("|len=").Append(c.MaxLength.ToString(CultureInfo.InvariantCulture))
              .Append("|prec=").Append(c.Precision.ToString(CultureInfo.InvariantCulture))
              .Append("|scale=").Append(c.Scale.ToString(CultureInfo.InvariantCulture))
              .Append("|null=").Append(Flag(c.IsNullable));
            if (c.Collation is not null && !options.IgnoreCollation) sb.Append("|coll=").Append(c.Collation);
            if (c.IsIdentity) sb.Append("|identity");
            if (c.IsComputed) sb.Append("|computed");
            sb.Append('\n');
        }
        return sb.ToString();
    }

    private static readonly HashSet<string> NumericPartitionTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "int", "bigint", "smallint", "tinyint", "decimal", "numeric",
        "money", "smallmoney", "float", "real", "bit",
    };

    /// <summary>Partition sınır değerini tipe göre literale çevirir: sayısal çıplak, ötekiler N'...'.</summary>
    private static string PartitionLiteral(string? type, string value)
    {
        if (value == "NULL") return "NULL";
        if (type is not null && NumericPartitionTypes.Contains(type)) return value;
        return $"N'{value.Replace("'", "''")}'";
    }

    private static string RenderSequence(ObjectKey key, SequenceRow s)
    {
        var type = s.TypeName.Equals("decimal", StringComparison.OrdinalIgnoreCase)
                || s.TypeName.Equals("numeric", StringComparison.OrdinalIgnoreCase)
            ? $"[{s.TypeName}]({s.Precision},{s.Scale})"
            : $"[{s.TypeName}]";

        var cache = s switch
        {
            { IsCached: true, CacheSize: > 0 } => $"CACHE {s.CacheSize}",
            { IsCached: true } => "CACHE",
            _ => "NO CACHE",
        };

        return $"""
            CREATE SEQUENCE [{key.Schema}].[{key.Name}]
                AS {type}
                START WITH {s.StartValue ?? "1"}
                INCREMENT BY {s.Increment ?? "1"}
                MINVALUE {s.MinValue ?? "0"}
                MAXVALUE {s.MaxValue ?? "0"}
                {(s.IsCycling ? "CYCLE" : "NO CYCLE")}
                {cache};
            """;
    }

    /// <summary>
    /// System-versioning kanoniği. History tablo adı otomatik üretilmişse
    /// (MSSQL_TemporalHistoryFor_&lt;object_id&gt;) ortamlar arasında farklıdır; bu yüzden
    /// otomatik adı sabit bir yer tutucuya indirger, yalnızca kullanıcı verdiği adı kıyaslar.
    /// </summary>
    private static string BuildTemporal(TemporalRow t)
    {
        string history;
        if (t.HistoryName is null) history = "-";
        else if (t.HistoryName.StartsWith("MSSQL_TemporalHistoryFor_", StringComparison.OrdinalIgnoreCase))
            history = "(auto)";
        else history = $"[{t.HistorySchema}].[{t.HistoryName}]";

        return $"temporal|versioning=ON|history={history}|start={t.StartColumn ?? "-"}|end={t.EndColumn ?? "-"}";
    }

    private static string BuildSequence(SequenceRow s) =>
        $"sequence|type={s.TypeName}|prec={s.Precision}|scale={s.Scale}" +
        $"|start={s.StartValue}|increment={s.Increment}|min={s.MinValue}|max={s.MaxValue}" +
        $"|cycling={Flag(s.IsCycling)}|cached={Flag(s.IsCached)}|cacheSize={s.CacheSize?.ToString(CultureInfo.InvariantCulture) ?? "-"}";

    // --- Yardımcılar ---

    private static void SetPart(ObjectSnapshot snapshot, string name, string canonical)
    {
        if (canonical.Length == 0) return;
        snapshot.PartCanonical[name] = canonical;
        snapshot.Parts[name] = Hash.Of(canonical);
    }

    /// <summary>
    /// Kullanıcı için okunur CREATE USER (detay paneli + script). Windows/AD principal'ları
    /// (U/G/E/X) ad ile eşleşir; SQL kullanıcısı (S) için login eşlemesi ortama özgü olduğundan
    /// WITHOUT LOGIN üretilir (DBA sonradan login'e bağlar).
    /// </summary>
    internal static string UserCreateScript(UserRow user)
    {
        var login = user.Type == "S" ? " WITHOUT LOGIN" : string.Empty;
        var schema = user.DefaultSchema is { Length: > 0 } s && !s.Equals("dbo", StringComparison.OrdinalIgnoreCase)
            ? $" WITH DEFAULT_SCHEMA=[{s}]" : string.Empty;
        return $"CREATE USER [{user.Name}]{login}{schema};";
    }

    private static void Finalize(ObjectSnapshot snapshot)
    {
        var ordered = snapshot.Parts.OrderBy(p => p.Key, StringComparer.Ordinal).ToList();
        snapshot.Hash = Hash.Combine(ordered.Select(p => p.Value));
        snapshot.Canonical = string.Join('\n',
            ordered.Select(p => $"-- [{p.Key}]\n{snapshot.PartCanonical[p.Key]}"));
    }

    private static string MarkIncomparable(ObjectSnapshot snapshot, string reason)
    {
        snapshot.Incomparable = true;
        snapshot.IncomparableReason = reason;
        return $"<incomparable: {reason}>";
    }

    private static string Column(Dictionary<long, string> names, int objectId, int columnId) =>
        names.GetValueOrDefault(Pair(objectId, columnId), $"#{columnId}");

    private static char Flag(bool value) => value ? '1' : '0';

    private static long Pair(int high, int low) => ((long)high << 32) | (uint)low;

    private static Dictionary<TKey, List<TValue>> GroupBy<TKey, TValue>(
        List<TValue> source, Func<TValue, TKey> keySelector) where TKey : notnull
    {
        var result = new Dictionary<TKey, List<TValue>>();
        foreach (var item in source)
        {
            var key = keySelector(item);
            if (!result.TryGetValue(key, out var list)) result[key] = list = [];
            list.Add(item);
        }
        return result;
    }
}
