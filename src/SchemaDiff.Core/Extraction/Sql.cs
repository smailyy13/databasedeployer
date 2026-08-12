namespace SchemaDiff.Core.Extraction;

/// <summary>
/// Katalog sorguları. Kural: obje başına sorgu YOK — obje sınıfı başına tek sorgu.
/// Hepsi salt okunur ve birbirinden bağımsız, dolayısıyla paralel koşabilir.
/// </summary>
internal static class Sql
{
    public const string Preflight = """
        SELECT
            CONVERT(nvarchar(256), SERVERPROPERTY('ServerName')),
            DB_NAME(),
            SERVERPROPERTY('ProductVersion'),
            CONVERT(int, HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION'));
        """;

    // schema_id 1..4 yerleşik (dbo, guest, INFORMATION_SCHEMA, sys),
    // 16384+ sabit veritabanı rollerine ait.
    public const string Schemas = """
        SELECT s.schema_id, s.name
        FROM sys.schemas AS s
        WHERE s.schema_id > 4 AND s.schema_id < 16384;
        """;

    // Temporal history tabloları (temporal_type=1) hariç: bunlar system-versioned tablonun
    // parçasıdır, ayrı obje olarak karşılaştırılmaz (adları çoğu zaman otomatik üretilir).
    public const string Objects = """
        SELECT o.object_id, s.name, o.name, RTRIM(o.type), o.modify_date, o.parent_object_id
        FROM sys.objects AS o
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        WHERE o.is_ms_shipped = 0
          AND o.type IN ('U','V','P','FN','IF','TF','TR','SN','SO')
          AND o.object_id NOT IN (SELECT ht.object_id FROM sys.tables AS ht WHERE ht.temporal_type = 1)
          -- SSMS "database diagram" destek objeleri (sysdiagrams, fn_diagramobjects, sp_*diagram):
          -- microsoft_database_tools_support extended property ile işaretli. SSDT gibi biz de atarız.
          AND NOT EXISTS (
              SELECT 1 FROM sys.extended_properties AS dtp
              WHERE dtp.class = 1 AND dtp.major_id = o.object_id AND dtp.minor_id = 0
                AND dtp.name = N'microsoft_database_tools_support');
        """;

    // System-versioned temporal tablolar (temporal_type=2): history tablosu + PERIOD kolonları.
    // sys.periods eski sürümlerde yok olabilir; sorgu opsiyonel çalışır.
    public const string Temporal = """
        SELECT t.object_id, SCHEMA_NAME(h.schema_id), h.name, sc.name, ec.name
        FROM sys.tables AS t
        LEFT JOIN sys.tables  AS h  ON h.object_id = t.history_table_id
        LEFT JOIN sys.periods AS p  ON p.object_id = t.object_id
        LEFT JOIN sys.columns AS sc ON sc.object_id = t.object_id AND sc.column_id = p.start_column_id
        LEFT JOIN sys.columns AS ec ON ec.object_id = t.object_id AND ec.column_id = p.end_column_id
        WHERE t.is_ms_shipped = 0 AND t.temporal_type = 2;
        """;

    public const string Modules = """
        SELECT m.object_id, m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier
        FROM sys.sql_modules AS m
        INNER JOIN sys.objects AS o ON o.object_id = m.object_id
        WHERE o.is_ms_shipped = 0;
        """;

    public const string Columns = """
        SELECT c.object_id, c.column_id, c.name,
               SCHEMA_NAME(tp.schema_id), tp.name,
               c.max_length, c.precision, c.scale,
               c.is_nullable, c.collation_name, c.is_identity, c.is_computed,
               dc.name, dc.definition, dc.is_system_named,
               cc.definition, cc.is_persisted,
               ic.seed_value, ic.increment_value,
               c.generated_always_type, c.is_hidden,
               c.is_sparse, c.is_filestream, c.is_rowguidcol, c.is_column_set,
               c.xml_collection_id, c.is_xml_document, ic.is_not_for_replication
        FROM sys.columns AS c
        INNER JOIN sys.objects AS o
            ON o.object_id = c.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V')
        INNER JOIN sys.types AS tp ON tp.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id
        LEFT JOIN sys.computed_columns AS cc
            ON cc.object_id = c.object_id AND cc.column_id = c.column_id
        LEFT JOIN sys.identity_columns AS ic
            ON ic.object_id = c.object_id AND ic.column_id = c.column_id;
        """;

    // Koleksiyonun içerdiği namespace'ler: içeriğin (XSD) okunamadığı durumda bile
    // "koleksiyon değişti mi?" sorusuna küme tabanlı bir cevap verir.
    public const string XmlSchemaNamespaces = """
        SELECT xsn.xml_collection_id, xsn.name
        FROM sys.xml_schema_namespaces AS xsn;
        """;

    // Koleksiyonun XSD içeriği. XML_SCHEMA_NAMESPACE tek yoldur; kolon argümanıyla
    // çalışmazsa sorgu düşer ve içerik olmadan devam ederiz (ad + namespace karşılaştırması
    // sürer, yalnız CREATE script'i üretilemez ve bu ismen bildirilir).
    public const string XmlSchemaCollectionContent = """
        SELECT xsc.xml_collection_id,
               CONVERT(nvarchar(max), XML_SCHEMA_NAMESPACE(SCHEMA_NAME(xsc.schema_id), xsc.name))
        FROM sys.xml_schema_collections AS xsc
        WHERE xsc.schema_id <> 4;
        """;

    // Tipli XML kolonlarının (xml(CONTENT [şema].[koleksiyon])) ad çözümü için.
    // Kolon yalnızca id tutar; ad olmadan script'te düz "xml" yazılır ve kolon YANLIŞ oluşur.
    public const string XmlSchemaCollections = """
        SELECT xsc.xml_collection_id, SCHEMA_NAME(xsc.schema_id), xsc.name
        FROM sys.xml_schema_collections AS xsc;
        """;

    // type = 0 heap'tir, karşılaştırılacak bir tanımı yok.
    // NOT: allow_row_locks/allow_page_locks SQL 2005+; optimize_for_sequential_key (2019+)
    // BİLİNÇLİ olarak alınmıyor — bu sorgu zorunlu, eski sunucuda düşerse karşılaştırma biter.
    public const string Indexes = """
        SELECT i.object_id, i.index_id, i.name, i.type_desc,
               i.is_unique, i.is_primary_key, i.is_unique_constraint,
               i.fill_factor, i.is_padded, i.ignore_dup_key, i.filter_definition,
               p.data_compression_desc,
               i.allow_row_locks, i.allow_page_locks
        FROM sys.indexes AS i
        INNER JOIN sys.objects AS o
            ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V')
        LEFT JOIN sys.partitions AS p
            ON p.object_id = i.object_id AND p.index_id = i.index_id AND p.partition_number = 1
        WHERE i.type NOT IN (0, 3, 4);
        """;

    // Index'in EK seçenekleri, AYRI ve opsiyonel sorguda:
    //   • optimize_for_sequential_key  SQL Server 2019 ile geldi
    //   • no_recompute (STATISTICS_NORECOMPUTE)  index'i taşıyan istatistikten okunur
    // Zorunlu Indexes sorgusuna konsaydı 2016/2017'de o sorgu düşer ve karşılaştırma
    // tümden biterdi. Burada düşerse yalnız bu iki ayar karşılaştırma dışı kalır.
    public const string IndexExtras = """
        SELECT i.object_id, i.index_id, i.optimize_for_sequential_key, st.no_recompute
        FROM sys.indexes AS i
        INNER JOIN sys.objects AS o
            ON o.object_id = i.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V')
        LEFT JOIN sys.stats AS st
            ON st.object_id = i.object_id AND st.stats_id = i.index_id
        WHERE i.type NOT IN (0, 3, 4);
        """;

    // XML index'ler (type 3). Genel index yolundan AYRI tutulur: sözdizimi tamamen farklıdır
    // (primary'de PRIMARY, secondary'de USING XML INDEX ... FOR ...), kolonlarda ASC/DESC yoktur.
    // using_xml_index_id NULL ise primary, aksi hâlde o primary'ye bağlı secondary'dir.
    public const string XmlIndexes = """
        SELECT xi.object_id, xi.index_id, xi.name, xi.using_xml_index_id,
               xi.secondary_type_desc, pi.name
        FROM sys.xml_indexes AS xi
        INNER JOIN sys.objects AS o
            ON o.object_id = xi.object_id AND o.is_ms_shipped = 0
        LEFT JOIN sys.xml_indexes AS pi
            ON pi.object_id = xi.object_id AND pi.index_id = xi.using_xml_index_id;
        """;

    // Spatial index'ler (type 4) + tessellation ayarları. GEOMETRY_GRID BOUNDING_BOX ister,
    // GEOGRAPHY_GRID istemez; AUTO_GRID çeşitlerinde GRIDS yazılmaz.
    public const string SpatialIndexes = """
        SELECT si.object_id, si.index_id, si.name, si.spatial_index_type_desc,
               t.bounding_box_xmin, t.bounding_box_ymin, t.bounding_box_xmax, t.bounding_box_ymax,
               t.level_1_grid_desc, t.level_2_grid_desc, t.level_3_grid_desc, t.level_4_grid_desc,
               t.cells_per_object
        FROM sys.spatial_indexes AS si
        INNER JOIN sys.objects AS o
            ON o.object_id = si.object_id AND o.is_ms_shipped = 0
        LEFT JOIN sys.spatial_index_tessellations AS t
            ON t.object_id = si.object_id AND t.index_id = si.index_id;
        """;

    public const string IndexColumns = """
        SELECT ic.object_id, ic.index_id, ic.index_column_id, ic.column_id,
               ic.key_ordinal, ic.is_descending_key, ic.is_included_column
        FROM sys.index_columns AS ic
        INNER JOIN sys.objects AS o
            ON o.object_id = ic.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V');
        """;

    // Full-text kataloglar: veritabanı seviyesi, şemasız objeler. Dosya yolu (path) ortama
    // özgüdür ve SQL 2008'den beri kullanılmaz — karşılaştırmaya girmez.
    public const string FullTextCatalogs = """
        SELECT ftc.fulltext_catalog_id, ftc.name, ftc.is_accent_sensitivity_on, ftc.is_default
        FROM sys.fulltext_catalogs AS ftc;
        """;

    // Full-text stoplist'ler: veritabanı seviyesi, şemasız objeler. Full-text index'ler
    // bunlara ADIYLA başvurur — hedefte yoksa index'in CREATE'i patlar.
    public const string FullTextStoplists = """
        SELECT sl.stoplist_id, sl.name
        FROM sys.fulltext_stoplists AS sl;
        """;

    public const string FullTextStopwords = """
        SELECT sw.stoplist_id, sw.stopword, sw.language_id
        FROM sys.fulltext_stopwords AS sw;
        """;

    // Full-text index: tablo başına EN FAZLA BİR tane olur, bu yüzden tablonun parçasıdır.
    // KEY INDEX (benzersiz, tek kolonlu, NOT NULL index) zorunludur; adıyla tutulur.
    // stoplist_id: NULL = OFF, 0 = SYSTEM, aksi hâlde kullanıcı stoplist'i.
    public const string FullTextIndexes = """
        SELECT fti.object_id, ki.name, ftc.name, fti.is_enabled,
               fti.change_tracking_state_desc, fti.stoplist_id, sl.name
        FROM sys.fulltext_indexes AS fti
        INNER JOIN sys.objects AS o
            ON o.object_id = fti.object_id AND o.is_ms_shipped = 0
        LEFT JOIN sys.indexes AS ki
            ON ki.object_id = fti.object_id AND ki.index_id = fti.unique_index_id
        LEFT JOIN sys.fulltext_catalogs AS ftc
            ON ftc.fulltext_catalog_id = fti.fulltext_catalog_id
        LEFT JOIN sys.fulltext_stoplists AS sl
            ON sl.stoplist_id = fti.stoplist_id;
        """;

    // Full-text index'e dahil kolonlar. type_column_id: binary kolonun uzantısını tutan
    // kolon (TYPE COLUMN); language_id: dil LCID'si.
    public const string FullTextIndexColumns = """
        SELECT ftc.object_id, ftc.column_id, ftc.type_column_id, ftc.language_id
        FROM sys.fulltext_index_columns AS ftc
        INNER JOIN sys.objects AS o
            ON o.object_id = ftc.object_id AND o.is_ms_shipped = 0;
        """;

    // Kullanıcı istatistikleri (CREATE STATISTICS). ÜÇ tür istatistik vardır:
    //   • index'in taşıdığı (user_created=0, auto_created=0) → index'in parçası, ayrı script'lenmez
    //   • otomatik üretilen (auto_created=1)                 → optimizer artefaktı, şema farkı değil
    //   • kullanıcının yazdığı (user_created=1)              → GERÇEK şema objesi, burada yalnız bu
    // is_incremental SQL Server 2014 ile geldi; sorgu opsiyonel çalışır.
    public const string Statistics = """
        SELECT st.object_id, st.stats_id, st.name, st.no_recompute, st.filter_definition, st.is_incremental
        FROM sys.stats AS st
        INNER JOIN sys.objects AS o
            ON o.object_id = st.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V')
        WHERE st.user_created = 1;
        """;

    // İstatistik kolonları, sıralı (stats_column_id istatistikteki kolon sırasıdır —
    // ilk kolon histogramı taşır, bu yüzden sıra anlamlıdır ve korunmalıdır).
    public const string StatisticColumns = """
        SELECT sc.object_id, sc.stats_id, sc.stats_column_id, sc.column_id
        FROM sys.stats_columns AS sc
        INNER JOIN sys.stats AS st ON st.object_id = sc.object_id AND st.stats_id = sc.stats_id
        INNER JOIN sys.objects AS o
            ON o.object_id = sc.object_id AND o.is_ms_shipped = 0 AND o.type IN ('U','V')
        WHERE st.user_created = 1;
        """;

    // PK/UQ constraint adları için: sistem tarafından üretilmiş adlar (PK__Tbl__A1B2C3)
    // ortamlar arasında farklıdır ve karşılaştırmaya girmemeli.
    public const string KeyConstraints = """
        SELECT kc.parent_object_id, kc.unique_index_id, kc.name, RTRIM(kc.type), kc.is_system_named
        FROM sys.key_constraints AS kc
        INNER JOIN sys.objects AS o ON o.object_id = kc.parent_object_id AND o.is_ms_shipped = 0;
        """;

    public const string ForeignKeys = """
        SELECT fk.object_id, fk.parent_object_id, fk.name, fk.is_system_named,
               fk.referenced_object_id,
               fk.delete_referential_action, fk.update_referential_action,
               fk.is_disabled, fk.is_not_trusted, fk.is_not_for_replication
        FROM sys.foreign_keys AS fk
        INNER JOIN sys.objects AS o ON o.object_id = fk.parent_object_id AND o.is_ms_shipped = 0;
        """;

    public const string ForeignKeyColumns = """
        SELECT fkc.constraint_object_id, fkc.constraint_column_id,
               fkc.parent_object_id, fkc.parent_column_id,
               fkc.referenced_object_id, fkc.referenced_column_id
        FROM sys.foreign_key_columns AS fkc
        INNER JOIN sys.objects AS o ON o.object_id = fkc.parent_object_id AND o.is_ms_shipped = 0;
        """;

    public const string CheckConstraints = """
        SELECT cc.parent_object_id, cc.name, cc.is_system_named,
               cc.definition, cc.is_disabled, cc.is_not_trusted, cc.is_not_for_replication
        FROM sys.check_constraints AS cc
        INNER JOIN sys.objects AS o ON o.object_id = cc.parent_object_id AND o.is_ms_shipped = 0;
        """;

    public const string Synonyms = """
        SELECT sn.object_id, sn.base_object_name
        FROM sys.synonyms AS sn
        INNER JOIN sys.objects AS o ON o.object_id = sn.object_id AND o.is_ms_shipped = 0;
        """;

    // current_value KASITLI olarak dışarıda: her kullanımda değişir, şema farkı değildir.
    public const string Sequences = """
        SELECT sq.object_id, TYPE_NAME(sq.user_type_id), sq.precision, sq.scale,
               sq.start_value, sq.increment, sq.minimum_value, sq.maximum_value,
               sq.is_cycling, sq.is_cached, sq.cache_size
        FROM sys.sequences AS sq
        INNER JOIN sys.objects AS o ON o.object_id = sq.object_id AND o.is_ms_shipped = 0;
        """;

    // Yaklaşık satır sayısı — tabloyu TARAMAZ, metadata'dan okur (milisaniyeler).
    // Deployment öncesi "bu tablo dolu, değişiklik bloklanır" uyarısı için.
    // VIEW DATABASE STATE yetkisi ister; yoksa çekim bu sorgu olmadan devam eder.
    public const string RowCounts = """
        SELECT ps.object_id, SUM(ps.row_count)
        FROM sys.dm_db_partition_stats AS ps
        INNER JOIN sys.objects AS o
            ON o.object_id = ps.object_id AND o.is_ms_shipped = 0 AND o.type = 'U'
        WHERE ps.index_id IN (0, 1)
        GROUP BY ps.object_id;
        """;

    // Modüller arası referanslar. Script üretiminde yeni objelerin doğru sırada
    // oluşturulması için gerekli. referenced_id NULL olanlar (çözülemeyen ya da
    // veritabanı dışı referanslar) sıralamaya katkı sağlamaz, dışarıda bırakılır.
    public const string Dependencies = """
        SELECT DISTINCT d.referencing_id, d.referenced_id
        FROM sys.sql_expression_dependencies AS d
        INNER JOIN sys.objects AS o
            ON o.object_id = d.referencing_id AND o.is_ms_shipped = 0
        WHERE d.referenced_id IS NOT NULL
          AND d.referenced_id <> d.referencing_id;
        """;

    // Dış (cross-database ya da linked server) referanslar. Bu objeler ancak dış kaynak
    // hedefte de varsa deploy edilebilir; deployment öncesi uyarı için.
    public const string ExternalReferences = """
        SELECT DISTINCT SCHEMA_NAME(o.schema_id), o.name,
               ISNULL(d.referenced_server_name, N''), ISNULL(d.referenced_database_name, N''),
               ISNULL(d.referenced_schema_name, N''), ISNULL(d.referenced_entity_name, N'')
        FROM sys.sql_expression_dependencies AS d
        INNER JOIN sys.objects AS o ON o.object_id = d.referencing_id AND o.is_ms_shipped = 0
        WHERE d.referenced_database_name IS NOT NULL OR d.referenced_server_name IS NOT NULL;
        """;

    // parent_class = 1 → tablo/view üzerindeki DML trigger'ları (DDL trigger'lar kapsam dışı).
    public const string Triggers = """
        SELECT tr.object_id, tr.is_disabled, tr.is_instead_of_trigger
        FROM sys.triggers AS tr
        INNER JOIN sys.objects AS o ON o.object_id = tr.object_id AND o.is_ms_shipped = 0
        WHERE tr.parent_class = 1;
        """;

    // Veritabanı seviyesi DDL trigger'ları (parent_class = 0). sys.objects'te DEĞİLler;
    // tanımları sys.sql_modules'ta. Şema yok. Audit/uyum için yaygın (banka EDW'lerinde).
    public const string DdlTriggers = """
        SELECT tr.name, tr.is_disabled, m.definition, m.uses_ansi_nulls, m.uses_quoted_identifier
        FROM sys.triggers AS tr
        LEFT JOIN sys.sql_modules AS m ON m.object_id = tr.object_id
        WHERE tr.parent_class = 0 AND tr.is_ms_shipped = 0;
        """;

    // Modül parametreleri: yalnız extended property'lerin ad çözümü için (class 2).
    public const string Parameters = """
        SELECT pr.object_id, pr.parameter_id, pr.name
        FROM sys.parameters AS pr
        INNER JOIN sys.objects AS o ON o.object_id = pr.object_id AND o.is_ms_shipped = 0;
        """;

    // Plan guide'lar: sorgu planı zorlamaları. sp_create_plan_guide ile kurulur, DDL değildir.
    // scope_object_id modül kapsamlı guide'larda dolu; ADIYLA tutulur (id ortama özgü).
    public const string PlanGuides = """
        SELECT pg.plan_guide_id, pg.name, pg.is_disabled,
               pg.scope_type_desc, OBJECT_SCHEMA_NAME(pg.scope_object_id), OBJECT_NAME(pg.scope_object_id),
               pg.scope_batch, pg.parameters, pg.hints, pg.query_text
        FROM sys.plan_guides AS pg;
        """;

    // Veritabanı seviyesi ayarlar (SQL 2016+). Yalnız VARSAYILANDAN SAPANLAR: varsayılanlar
    // sürümle değişir, hepsini kıyaslamak sürüm farkını şema farkı gibi gösterirdi.
    public const string DatabaseScopedConfigurations = """
        SELECT dsc.configuration_id, dsc.name,
               CONVERT(nvarchar(256), dsc.value), CONVERT(nvarchar(256), dsc.value_for_secondary)
        FROM sys.database_scoped_configurations AS dsc
        WHERE dsc.is_value_default = 0;
        """;

    // Legacy CREATE RULE / CREATE DEFAULT objeleri (SQL 2005'ten beri kullanımdan kalkmış
    // ama eski EDW'lerde hâlâ var). Tanımları sys.sql_modules'ta; bağlandıkları kolonlar ayrı.
    public const string LegacyRuleDefaults = """
        SELECT o.object_id, s.name, o.name, RTRIM(o.type), m.definition
        FROM sys.objects AS o
        INNER JOIN sys.schemas AS s ON s.schema_id = o.schema_id
        LEFT JOIN sys.sql_modules AS m ON m.object_id = o.object_id
        WHERE o.is_ms_shipped = 0 AND o.type IN ('R', 'D') AND o.parent_object_id = 0;
        """;

    // Kullanıcı tanımlı alias tipler (CREATE TYPE dbo.Money FROM decimal(19,4)).
    // CLR assembly tipleri ve table type'lar hariç. Baz sistem tipi adıyla tutulur.
    public const string UserDefinedTypes = """
        SELECT t.user_type_id, SCHEMA_NAME(t.schema_id), t.name,
               TYPE_NAME(t.system_type_id), t.max_length, t.precision, t.scale,
               t.is_nullable, t.collation_name
        FROM sys.types AS t
        WHERE t.is_user_defined = 1 AND t.is_table_type = 0 AND t.is_assembly_type = 0;
        """;

    // Partition function'lar: aralık yönü (LEFT/RIGHT) + girdi tipi. Sınır değerleri ayrı sorguda.
    public const string PartitionFunctions = """
        SELECT pf.function_id, pf.name, pf.boundary_value_on_right, TYPE_NAME(pp.system_type_id)
        FROM sys.partition_functions AS pf
        LEFT JOIN sys.partition_parameters AS pp ON pp.function_id = pf.function_id;
        """;

    // Partition function sınır değerleri, sıralı. value sql_variant → nvarchar.
    public const string PartitionRangeValues = """
        SELECT prv.function_id, prv.boundary_id, CONVERT(nvarchar(4000), prv.value)
        FROM sys.partition_range_values AS prv
        ORDER BY prv.function_id, prv.boundary_id;
        """;

    // Partition scheme'ler: hangi partition function'a bağlı.
    public const string PartitionSchemes = """
        SELECT ps.data_space_id, ps.name, pf.name
        FROM sys.partition_schemes AS ps
        INNER JOIN sys.partition_functions AS pf ON pf.function_id = ps.function_id;
        """;

    // Scheme'in partition → filegroup eşlemesi, sıralı.
    public const string PartitionSchemeFiles = """
        SELECT dds.partition_scheme_id, dds.destination_id, fg.name
        FROM sys.destination_data_spaces AS dds
        INNER JOIN sys.filegroups AS fg ON fg.data_space_id = dds.data_space_id
        ORDER BY dds.partition_scheme_id, dds.destination_id;
        """;

    // Table type'lar (CREATE TYPE dbo.IdList AS TABLE(...)). Kolonları ayrı sorguda çekilir;
    // type_table_object_id iç objeye (kolonların bağlı olduğu) işaret eder.
    public const string TableTypes = """
        SELECT tt.user_type_id, SCHEMA_NAME(tt.schema_id), tt.name, tt.type_table_object_id
        FROM sys.table_types AS tt
        WHERE tt.is_user_defined = 1;
        """;

    // Table type kolonları. object_id = table type'ın type_table_object_id'si.
    public const string TableTypeColumns = """
        SELECT c.object_id, c.column_id, c.name,
               SCHEMA_NAME(tp.schema_id), tp.name,
               c.max_length, c.precision, c.scale,
               c.is_nullable, c.collation_name, c.is_identity, c.is_computed,
               dc.definition
        FROM sys.columns AS c
        INNER JOIN sys.table_types AS tt ON tt.type_table_object_id = c.object_id
        INNER JOIN sys.types AS tp ON tp.user_type_id = c.user_type_id
        LEFT JOIN sys.default_constraints AS dc ON dc.object_id = c.default_object_id;
        """;

    // Table type'ı PARAMETRE olarak kullanan modüller. Tip ALTER edilemez; yeniden kurmak
    // için önce bu modüller düşürülmeli. sys.sql_expression_dependencies bunu GÖRMEZ —
    // parametre tipi bir ifade bağımlılığı değildir, bu yüzden ayrı sorgu şart.
    public const string TableTypeDependents = """
        SELECT pr.user_type_id, pr.object_id
        FROM sys.parameters AS pr
        INNER JOIN sys.table_types AS tt ON tt.user_type_id = pr.user_type_id
        INNER JOIN sys.objects AS o ON o.object_id = pr.object_id AND o.is_ms_shipped = 0;
        """;

    // Table type'ın PK/UNIQUE ve (SQL 2014+) bağımsız index'leri. Table type ALTER edilemez;
    // bunlar tipin TANIMININ parçasıdır, farkları görünmezse tip "aynı" sanılır.
    public const string TableTypeIndexes = """
        SELECT i.object_id, i.index_id, i.name, i.type_desc,
               i.is_unique, i.is_primary_key, i.is_unique_constraint,
               kc.name, kc.is_system_named
        FROM sys.indexes AS i
        INNER JOIN sys.table_types AS tt ON tt.type_table_object_id = i.object_id
        LEFT JOIN sys.key_constraints AS kc
            ON kc.parent_object_id = i.object_id AND kc.unique_index_id = i.index_id
        WHERE i.type <> 0;
        """;

    public const string TableTypeIndexColumns = """
        SELECT ic.object_id, ic.index_id, ic.index_column_id, ic.column_id,
               ic.key_ordinal, ic.is_descending_key, ic.is_included_column
        FROM sys.index_columns AS ic
        INNER JOIN sys.table_types AS tt ON tt.type_table_object_id = ic.object_id;
        """;

    public const string TableTypeChecks = """
        SELECT cc.parent_object_id, cc.name, cc.is_system_named, cc.definition
        FROM sys.check_constraints AS cc
        INNER JOIN sys.table_types AS tt ON tt.type_table_object_id = cc.parent_object_id;
        """;

    // Kullanıcı tanımlı veritabanı rolleri. Sabit roller (db_owner vb.) ve public dışarıda —
    // onlar her veritabanında aynıdır, şema farkı değildir. Owner adıyla (id değil) tutulur.
    // Kullanıcı tanımlı roller VE sabit (fixed) roller. Sabit rollerin tanımı değişmez ama
    // ÜYELİKLERİ değişir (ör. db_datareader'a kullanıcı eklenmesi) — SSDT de bunu yakalar.
    public const string Roles = """
        SELECT dp.principal_id, dp.name, USER_NAME(dp.owning_principal_id), dp.is_fixed_role
        FROM sys.database_principals AS dp
        WHERE dp.type = 'R' AND dp.name <> N'public'
          AND (dp.is_fixed_role = 1 OR dp.principal_id > 4);
        """;

    // Rol üyelikleri (kullanıcı tanımlı + sabit). Üye adıyla tutulur — kullanıcı ya da rol.
    public const string RoleMembers = """
        SELECT rm.role_principal_id, USER_NAME(rm.member_principal_id)
        FROM sys.database_role_members AS rm;
        """;

    // Veritabanı kullanıcıları: SQL (S), Windows kullanıcı (U) / grup (G), external (E/X).
    // Login/SID eşlemesi ortama özgüdür — karşılaştırmaya girmez; ad + tip + default schema.
    public const string Users = """
        SELECT dp.name, dp.type, dp.default_schema_name, dp.principal_id
        FROM sys.database_principals AS dp
        WHERE dp.type IN ('S','U','G','E','X') AND dp.principal_id > 4
          AND dp.name NOT IN (N'dbo', N'guest', N'INFORMATION_SCHEMA', N'sys');
        """;

    // Obje/kolon (class 1) ve şema (class 3) seviyesi izinler. Grantee adıyla (id değil).
    // state_desc: GRANT / GRANT_WITH_GRANT_OPTION / DENY. Sistem objelerininki dışarıda.
    public const string Permissions = """
        SELECT CAST(1 AS tinyint) AS class, p.major_id, p.minor_id,
               p.permission_name, p.state_desc, USER_NAME(p.grantee_principal_id)
        FROM sys.database_permissions AS p
        INNER JOIN sys.objects AS o ON o.object_id = p.major_id AND o.is_ms_shipped = 0
        WHERE p.class = 1
        UNION ALL
        SELECT CAST(3 AS tinyint), p.major_id, p.minor_id,
               p.permission_name, p.state_desc, USER_NAME(p.grantee_principal_id)
        FROM sys.database_permissions AS p
        INNER JOIN sys.schemas AS s
            ON s.schema_id = p.major_id AND s.schema_id > 4 AND s.schema_id < 16384
        WHERE p.class = 3
        UNION ALL
        SELECT CAST(0 AS tinyint), 0, 0, p.permission_name, p.state_desc, USER_NAME(p.grantee_principal_id)
        FROM sys.database_permissions AS p
        WHERE p.class = 0;
        """;

    // Extended property'ler (MS_Description vb.). class 1 = obje/kolon (major=object_id,
    // minor=0 obje kendisi, minor>0 kolon), class 3 = şema (major=schema_id). Host obje
    // sınıfına göre filtrelenir; sistem objelerininki dışarıda kalır. value sql_variant
    // olduğu için nvarchar'a çevrilir — karşılaştırma metin üzerinden yapılır.
    public const string ExtendedProperties = """
        SELECT CAST(1 AS tinyint) AS class, ep.major_id, ep.minor_id, ep.name,
               CONVERT(nvarchar(4000), ep.value) AS value
        FROM sys.extended_properties AS ep
        INNER JOIN sys.objects AS o ON o.object_id = ep.major_id AND o.is_ms_shipped = 0
        WHERE ep.class = 1
        UNION ALL
        SELECT CAST(3 AS tinyint), ep.major_id, ep.minor_id, ep.name,
               CONVERT(nvarchar(4000), ep.value)
        FROM sys.extended_properties AS ep
        INNER JOIN sys.schemas AS s
            ON s.schema_id = ep.major_id AND s.schema_id > 4 AND s.schema_id < 16384
        WHERE ep.class = 3
        UNION ALL
        SELECT CAST(0 AS tinyint), 0, 0, ep.name, CONVERT(nvarchar(4000), ep.value)
        FROM sys.extended_properties AS ep
        WHERE ep.class = 0
        UNION ALL
        -- class 2: parametre (major=object_id, minor=parameter_id)
        SELECT CAST(2 AS tinyint), ep.major_id, ep.minor_id, ep.name,
               CONVERT(nvarchar(4000), ep.value)
        FROM sys.extended_properties AS ep
        INNER JOIN sys.objects AS o ON o.object_id = ep.major_id AND o.is_ms_shipped = 0
        WHERE ep.class = 2
        UNION ALL
        -- class 4: veritabanı principal'ı (major=principal_id)
        SELECT CAST(4 AS tinyint), ep.major_id, ep.minor_id, ep.name,
               CONVERT(nvarchar(4000), ep.value)
        FROM sys.extended_properties AS ep
        INNER JOIN sys.database_principals AS dp ON dp.principal_id = ep.major_id
        WHERE ep.class = 4 AND dp.principal_id > 4
        UNION ALL
        -- class 7: index (major=object_id, minor=index_id)
        SELECT CAST(7 AS tinyint), ep.major_id, ep.minor_id, ep.name,
               CONVERT(nvarchar(4000), ep.value)
        FROM sys.extended_properties AS ep
        INNER JOIN sys.objects AS o ON o.object_id = ep.major_id AND o.is_ms_shipped = 0
        WHERE ep.class = 7;
        """;
}
