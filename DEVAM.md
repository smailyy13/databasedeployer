# DEVAM — Nerede Kaldık (kapsam genişletme çalışması)

> Bu dosya, başka bir makinede kaldığın yerden devam edebilmen için yazıldı.
> Son güncelleme: 2026-08-12 (Dalga 13 sonrası)

## Hızlı durum
- **Repo:** `smailyy13/databasedeployer` (GitHub, private) — proje adı **SchemaDiff**
- **Branch:** `main`
- **Testler:** **763 (760 yeşil + 3 atlanan entegrasyon)**
- **Son release:** **v1.4** (portable, 4 parça). **v1.5 HENÜZ ÇIKARILMADI** — aşağıya bak.
- **Gereken SDK:** .NET 10 (`dotnet-install.sh --channel 10.0`; macOS'ta `~/.dotnet`).

## Başka makinede devam etmek için
```bash
git clone https://github.com/smailyy13/databasedeployer.git
cd databasedeployer            # (klasör adı repo adı)
git pull                        # en güncel main

# Derle + test
dotnet build -c Release
dotnet test  -c Release --no-build      # 760 geçer, 3 atlanır (entegrasyon, canlı SQL ister)

# Web arayüzünü çalıştır (yerel, sadece 127.0.0.1)
dotnet run -c Release --project src/SchemaDiff.Web -- --port 5290
# → tarayıcıda http://127.0.0.1:5290
```
> Not: `jobs.json` **gitignore'da** (bağlantı dizeleri içerir, repoda YOK). Yeni makinede
> kendi bağlantılarını arayüzden gireceksin.

## Önceki oturumda tamamlananlar (hepsi commit + push)
1. **Deploy script sağlamlık** (`b40cd0c`): script başına `USE [hedef DB]` (reverse-safe),
   tüm bölümlerde `IF @@TRANCOUNT > 0 COMMIT` (Msg 3902 önlenir), `DROP USER` şema devri
   (Msg 15138), rol detayında okunabilir `ALTER ROLE ADD MEMBER`, **DDL trigger scriptleme**
   (`ON DATABASE` — önceden kapsam dışıydı).
2. **Dalga 1 — CoverageProbe** (`f982504`): 17 yeni sayaç. Araç artık bir DB'de şu sınıfların
   KAÇ tane olduğunu ölçüp "var ama üretmiyorum" diye uyarıyor: sıkıştırılmış partition,
   sparse/filestream/rowguidcol kolon, application role, RLS, DDM, sertifika/anahtarlar,
   DB-scoped credential, Always Encrypted (CMK/CEK), Service Broker, XML schema collection,
   plan guide, PolyBase external, DB-scoped config, legacy RULE/DEFAULT.
3. **Dalga 2 — DATA_COMPRESSION** (`f982504`): index + PK/UNIQUE üzerinde
   `WITH (DATA_COMPRESSION = ROW/PAGE)` extraction+karşılaştırma+scriptleme. Yeni ayar
   "Veri sıkıştırmayı yok say".
4. **Dalga 3 — Temporal** (`3b12de5`): kolonlarda `generated_always_type`+`is_hidden`
   çıkarımı; CREATE TABLE artık tam temporal üretiyor (`PERIOD FOR SYSTEM_TIME` +
   `WITH (SYSTEM_VERSIONING = ON [(HISTORY_TABLE=...)])`); değişen tabloda **KAPATMA**
   tam ve güvenli (SET OFF + DROP PERIOD); **AÇMA/yeniden kurulum** riskli olduğu için
   (PERIOD kolonu + DEFAULT gerektirir) elle uygulanmak üzere uyarıyla atlanıyor.

## Bu oturumda tamamlananlar (Dalga 4–13)

Karar noktasında **(B)** seçildi: kullanıcı istatistikleri scriptlemesi eklendi.

5. **Dalga 4 — Kullanıcı istatistikleri (`CREATE STATISTICS`)**: uçtan uca kapsam.
   - **Çekim:** `Sql.Statistics` + `Sql.StatisticColumns` (yalnız `user_created = 1` —
     otomatik üretilenler optimizer artefaktı, index'inkiler index'in parçası).
   - **Karşılaştırma:** tablo ve view'da yeni `statistics` kanonik parçası; kolon SIRASI
     anlamlı (ilk kolon histogramı taşır). Yeni ayar: **"Kullanıcı istatistiklerini yok say"**
     (`IgnoreStatistics`, varsayılan KAPALI — eksik istatistik sorgu planını değiştirir).
   - **Scriptleme:** yeni tabloda `CREATE TABLE` sonrası ayrı batch'te; değişen tabloda
     drop → (kolon değişiklikleri) → create sırasıyla. `WHERE` filtresi, `NORECOMPUTE`,
     `INCREMENTAL = ON` üretiliyor. Örnekleme oranı **bilinçli yazılmıyor** (katalogda
     tutulmaz, şema farkı değildir — SSDT de yazmaz).
   - **Sürüm güvenliği:** `sys.stats.is_incremental` SQL 2014+ olduğundan sorgu düşebilir.
     Düşerse sınıf "boş" sayılmaz, **karşılaştırma dışı** bırakılır + uyarı verilir —
     aksi hâlde hedefteki istatistikler için sahte `DROP STATISTICS` üretilirdi.
     Bunun için `ExtractionReport.FailedQueries` eklendi (tüm opsiyonel sorgular için).
   - **CoverageProbe:** "Kullanıcı istatistikleri" artık *covered*; yeni sayaç
     "Otomatik istatistikler (kapsam dışı)".
   - **Arayüz:** değişiklikler ağaçta kendi **Statistics** klasöründe.

6. **Dalga 5 — Kolon depolama nitelikleri (SPARSE · FILESTREAM · ROWGUIDCOL · COLUMN_SET)**:
   ötekilerden farklı olarak bu bir "eksik obje" değil, **yanlış CREATE TABLE** boşluğuydu —
   `sys.columns`'tan hiç çekilmediği için tablo hedefte oluşuyor ama başka bir tablo oluyordu.
   - Çekim + kanonik (nitelik yoksa yazılmaz → eski hash'ler değişmez) + `CREATE TABLE` + `ADD COLUMN`.
   - Açma/kapama: SPARSE ve ROWGUIDCOL `ALTER COLUMN … {ADD|DROP}` ile üretiliyor.
     Tip de değişiyorsa SPARSE o ifadenin içinde (gramer NULL/NOT NULL'dan sonra ister);
     kapatma her hâlde açıkça yazılıyor — yazmamak "kaldır" demek değil.
   - FILESTREAM ve COLUMN_SET ALTER ile değiştirilemez → üretmek yerine ismen atlanıp bildiriliyor.
   - Ağaçta kolonun altında hangi niteliğin değiştiği yazıyor; CoverageProbe'da artık *covered*.

7. **Dalga 6 — Tipli XML · NOT FOR REPLICATION · index kilit seçenekleri**: yine "yanlış DDL"
   sınıfı, üçü birden.
   - **Tipli XML kolonları:** `xml_collection_id` + `is_xml_document` çekiliyor; yeni
     `Sql.XmlSchemaCollections` sorgusuyla id → `[şema].[koleksiyon]` çözülüyor.
     Karşılaştırma ve script ADI kullanıyor (id ortama özgü). Ad çözülemezse düz `xml`
     yazılıyor — uydurulmuyor. Öncesinde iki taraf da "xml" görünüp fark kaçıyordu.
   - **NOT FOR REPLICATION:** IDENTITY, CHECK ve FK üzerinde. IDENTITY'nin bayrağı ALTER ile
     değiştirilemediği için değişimi ismen atlanıp bildiriliyor.
   - **Bulunan yan hata:** `ADD COLUMN` yolunda IDENTITY hiç yazılmıyordu — identity kolonu
     hedefe sıradan kolon olarak ekleniyordu. `ColumnInfo` artık seed/increment taşıyor, düzeldi.
   - **Index kilit seçenekleri:** `allow_row_locks` / `allow_page_locks`. Yalnız KAPALI
     olduklarında yazılıyor (varsayılan ON → mevcut hash ve script'ler değişmiyor) ve
     DATA_COMPRESSION ile **tek** `WITH (...)` listesinde birleşiyor.
     `optimize_for_sequential_key` (2019+) bilinçli ALINMADI: `Indexes` sorgusu zorunlu,
     eski sunucuda düşerse karşılaştırma tümden biter.

8. **Dalga 7 — Constraint DURUMU script'te**: `is_disabled` / `is_not_trusted` zaten
   karşılaştırılıyordu ama üretilen script'e yansımıyordu — kaynakta bilerek kapatılmış bir
   CHECK hedefte AKTİF kuruluyordu. Script farkı doğru görüp yanlış tarafa taşıyordu.
   - Üç hâlin T-SQL yazımı farklı, üçü de üretiliyor: pasif → `NOCHECK CONSTRAINT`,
     aktif+güvenilmez → `CHECK CONSTRAINT`, aktif+güvenilir → `WITH CHECK CHECK CONSTRAINT`
     (mevcut veriyi tarar).
   - **Yalnız durum değiştiyse drop+recreate YOK** — hedef ifade tek satır. (Gereksiz
     DROP+ADD, FK referansı olan bir constraint'te deploy'u patlatırdı.)
   - Yeni eklenen pasif/güvenilmez constraint `WITH NOCHECK` ile ekleniyor: kaynakta
     kapalıysa mevcut veri onu ihlal ediyor olabilir, `WITH CHECK` deploy'u patlatırdı.
     Bu, kullanıcının "yeni constraint'leri doğrula" seçeneğini bilinçli olarak ezer.
   - Yeni tabloda da: `CREATE TABLE` içindeki constraint hep AKTİF doğar, pasif olanlar
     tablo oluştuktan sonra ayrı batch'te kapatılıyor.

9. **Dalga 8 — Full-text katalog + index** (KAPSAM.md yapılacaklar listesinin 1. maddesi):
   - **Katalog** yeni obje sınıfı (`ObjectKind.FullTextCatalog`), veritabanı seviyesi/şemasız.
     Tip üretecinde **en önce** kuruluyor (öncelik -1): tabloların index'i ona bağlı.
     Dosya yolu (path) kıyasa girmiyor — ortama özgü ve SQL 2008'den beri kullanılmıyor.
   - **Index** tablonun parçası (tablo başına en fazla bir tane): `KEY INDEX`, katalog,
     `TYPE COLUMN`, `LANGUAGE`, `CHANGE_TRACKING`, `STOPLIST`, pasiflik.
   - "Değişti" hâli yok: ya eklenir, ya düşer, ya baştan kurulur. Drop index'lerden ÖNCE,
     create SONRA (KEY INDEX'e bağlı).
   - `CREATE FULLTEXT INDEX` her zaman AKTİF doğar → pasif index ayrıca `DISABLE` ediliyor.
   - LCID 0 ("sunucu varsayılanı") yazılmıyor: yazmak ortama bağımlılık yaratırdı.
   - `STOPLIST`: `OFF`/`SYSTEM` anahtar kelime, kullanıcı stoplist'i köşeli parantezli.
   - KEY INDEX ya da katalog adı okunamazsa parça HİÇ yazılmıyor — yarım kanonik sahte fark üretir.

10. **Dalga 9 — Table type constraint / index / DEFAULT'ları**: tip ALTER edilemediği için
    buradaki değer script değil **GÖRÜNÜRLÜK** — önceden yalnız kolon yapısı kıyaslandığından,
    PK'sı ya da CHECK'i farklı iki tip "aynı" görünüyordu.
    - PK / UNIQUE / CHECK / bağımsız index (SQL 2014+ satır içi `INDEX`) + kolon DEFAULT'ları.
    - Kanonik ve `CREATE TYPE` gövdesi **aynı listeden** üretiliyor — ikisi ayrışamaz.
    - Sistem üretimi constraint adları yazılmıyor (table type'ta neredeyse hepsi öyle);
      kullanıcı adı verdiyse korunuyor.
    - Ayrı sorgular eklendi (`TableTypeIndexes/IndexColumns/Checks`): mevcut tablo
      sorgularını gevşetmek yerine açık ve izole tutuldu.

11. **Dalga 10 — XML ve spatial index'ler**: bu bir eksik DEĞİL, **bozuk SQL üretimi**ydi.
    `sys.indexes` bunları da döndürdüğü için genel index yoluna düşüyorlar ve
    `CREATE XML INDEX [x] ON t ([col] ASC);` üretiliyordu — geçersiz T-SQL (XML index'te
    ASC/DESC yok, primary'de `PRIMARY`, secondary'de `USING XML INDEX` zorunlu).
    - `Sql.Indexes` artık `type NOT IN (0, 3, 4)`; ikisi kendi sorgularından geliyor.
    - XML: primary/secondary ayrımı, `FOR PATH|VALUE|PROPERTY`. **Sıra zorunlu:** create'te
      primary önce, drop'ta secondary önce — tersi "cannot drop, it is used by…" verir.
    - Spatial: `BOUNDING_BOX` yalnız GEOMETRY'de, `GRIDS` yalnız AUTO_GRID DEĞİLKEN,
      `CELLS_PER_OBJECT`. Koordinatlar InvariantCulture ile yazılıyor — Türkçe kültürde
      "0,5" SQL'i bozardı.
    - Primary'si okunamayan secondary atlanıyor (USING yazılamaz).

12. **Dalga 11 — Arayüz/rapor uçlarının bağlanması** ("eksik kalan var mı?" taraması):
    - **Tür adı ↔ ObjectKind çevrimi kopuktu.** Ağaçtaki ad kullanıcı kutuyu işaretleyince
      sunucuya dönüyor ve türe çevriliyor; çevrim yalnız BOŞLUK atıyordu, tire atmıyordu.
      Sonuç: `User-Defined Type` (eskiden beri) ve `Full-Text Catalog` (Dalga 8) çözülemiyor,
      obje işaretlense bile script'e SESSİZCE girmiyor ve detay paneli boş dönüyordu.
      İki yön artık tek dosyada (`ObjectKindLabels`), her tür için gidiş-dönüş test ediliyor.
    - **`HandledElsewhere` kümesi Web ve CLI'de elle kopyalanmıştı**, `FullTextCatalog` ikisinde
      de eksikti: katalog script'e giriyor ama başlıkta "kapsam dışı" listeleniyordu.
      Artık `TypeScriptGenerator.HandledKinds` tek kaynak.
    - Detay paneli ile seçim artık AYNI çevrimi kullanıyor (önce iki ayrı ayrıştırma vardı).

13. **Dalga 12 — `STATISTICS_NORECOMPUTE` + `OPTIMIZE_FOR_SEQUENTIAL_KEY`**: Dalga 6'da
    bilinçli ertelenmişti (2019+ kolon, zorunlu `Indexes` sorgusuna konamaz).
    - Ayrı ve **opsiyonel** `Sql.IndexExtras` sorgusu: düşerse yalnız bu iki ayar kapsam
      dışı kalır, karşılaştırma sürer.
    - Düştüğünde "kapalı" DEĞİL "bilinmiyor" sayılıyor + uyarı. 2019 kaynak ↔ 2016 hedef
      karşılaştırmasında boş saymak her index için sahte fark ve gereksiz DROP+CREATE üretirdi.
    - İkisi de varsayılan KAPALI, yalnız açıkken yazılıyor → mevcut hash'ler ve script'ler
      değişmedi.

14. **Dalga 13 — XML schema collection**: yeni obje sınıfı (`ObjectKind.XmlSchemaCollection`),
    şemalı. Tipli XML kolonları (Dalga 6) buna ADIYLA başvuruyor — hedefte yoksa tablonun
    CREATE'i patlar, dolayısıyla tespit tek başına değerli.
    - Tip üretecinde **en önce** (öncelik -2): tipli XML kolonu ona bağlı.
    - **İki katmanlı karşılaştırma:** namespace listesi (küme tabanlı, her zaman okunur) +
      XSD içeriği (`XML_SCHEMA_NAMESPACE`, opsiyonel). İçerik okunamazsa ad + namespace
      karşılaştırması SÜRER, yalnız CREATE üretilemez ve obje ismen "atlandı" listesine düşer.
      Namespace'i aynı kalıp içeriği değişen koleksiyon ancak içerik katmanıyla yakalanır.
    - XSD içindeki tek tırnaklar kaçırılıyor; kaçırılmazsa string literal kapanır ve SQL bozulur.
    - Değişen koleksiyon script'lenmiyor: `ALTER XML SCHEMA COLLECTION` yalnız EKLEYEBİLİR,
      çıkarma yoktur — fark görünür, uygulama elle.

15. **Test:** 276 → **763** (760 yeşil + 3 atlanan entegrasyon).
   - `StatisticsTests` (33): karşılaştırma, sıra, filtre/NORECOMPUTE/INCREMENTAL,
     drop-önce/create-sonra sıralaması, yeni tablo script'i, okunamayan sorgu davranışı.
   - `CatalogQueryTests` (yeni): TÜM katalog sorgularının yapısal denetimi — en önemlisi
     **salt-okunurluk** (yazan bir ifade sızarsa yalnız bu test yakalar), ayrıca
     `SELECT *` yasağı, parantez dengesi, yalnız `sys.*` okuma, `is_ms_shipped = 0` filtresi.
     Denetçinin kendisi de test ediliyor (bilerek bozuk SQL'i yakalıyor mu).
   - `ColumnAttributeTests` (22): nitelik farkı, CREATE/ADD COLUMN yazımı, gramer sırası
     (FILESTREAM → SPARSE), aç/kapa ifadeleri, tip değişimiyle birlikte davranış,
     FILESTREAM/COLUMN_SET'in script yerine uyarıya düşmesi.
   - `TypedXmlAndReplicationTests` (19): koleksiyonun ADLA karşılaştırılması, CONTENT/DOCUMENT,
     NFR yazımı, ADD COLUMN'da IDENTITY, tek WITH listesi.
   - `ConstraintStateTests` (16): üç durum ifadesi, durum-farkında drop+recreate olmaması,
     yeni constraint'in WITH NOCHECK ile eklenmesi, yeni tabloda kapatma sırası.

### Sıradaki adım
Commit + push, sonra **v1.5 release** (aşağıdaki publish adımları).

### Kalan scriptlenebilir maddeler (düşük öncelik)
- `OPTIMIZE_FOR_SEQUENTIAL_KEY` (2019+) ve `STATISTICS_NORECOMPUTE` (index istatistiği)
- Full-text katalog + index — ayrı obje sınıfı
- Table type constraint/index'leri (şu an yalnız kolon yapısı)
- XML schema collection / plan guide / application role / DB scoped configuration / RULE-DEFAULT
- XML index / Spatial index — index handling'in uzantısı
- Fiziksel yerleşim (`ON [filegroup]`) — **bilinçli ertelendi:** filegroup'ları biz
  oluşturmuyoruz, `ON [DATA_FG]` yazarsak hedefte o FG yoksa CREATE patlar.

### Tespit-only kalacaklar (banka için oto-script RİSKLİ)
RLS (security policy), Service Broker, kripto anahtarlar/sertifikalar, PolyBase external,
CLR, Dynamic Data Masking, Always Encrypted. CoverageProbe bunları sayıyor; scriptlemiyoruz.

## Eğer v1.5 release çıkaracaksan (portable, part part)
```bash
# 1) publish
rm -rf publish-portable
dotnet publish src/SchemaDiff.Web -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish-portable
rm -f publish-portable/*.pdb
# 2) zip (PowerShell): Compress-Archive publish-portable/* SchemaDiff-v1.5-portable.zip
# 3) 12MB parçalara böl:  split -b 12m -d SchemaDiff-v1.5-portable.zip SchemaDiff-v1.5-portable.zip.part
# 4) gh release create v1.5 --title "SchemaDiff v1.5 (portable)" --notes-file <notlar> <part'lar>
# Birleştirme (Windows): copy /b part00+part01+part02+part03 SchemaDiff-v1.5-portable.zip
```

## Dalga 4'te değişen dosyalar (istatistik)
- `src/SchemaDiff.Core/Extraction/Sql.cs` — `Statistics`, `StatisticColumns` sorguları
- `src/SchemaDiff.Core/Extraction/CatalogRows.cs` — `StatisticRow`, `StatisticColumnRow`
- `src/SchemaDiff.Core/Extraction/CatalogExtractor.cs` — iki opsiyonel sorgu + `FailedQueries`
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — `IgnoreStatistics`, `BuildStatistics`,
  `BuildStatisticsDefinitions`, okunamayan sorgu koruması
- `src/SchemaDiff.Core/Model/Snapshot.cs` — `StatisticsDefinition`, `ExtractionReport.FailedQueries`
- `src/SchemaDiff.Core/Scripting/StatisticsScript.cs` — **yeni**: CREATE/DROP metni (tek kaynak)
- `src/SchemaDiff.Core/Scripting/TableScriptWriter.cs` — yeni tabloda `WriteStatistics`
- `src/SchemaDiff.Core/Scripting/TableScriptGenerator.cs` — `AppendStatisticsDiff` + `Sig`
- `src/SchemaDiff.Core/Analysis/ChangeCatalog.cs` — "Statistics" klasörü
- `src/SchemaDiff.Web/{Contracts,CompareService}.cs` + `wwwroot/app.js` — `IgnoreStatistics` ayarı
- Testler: `StatisticsTests` (yeni), `CatalogQueryTests` (yeni)

## Dalga 5'te değişen dosyalar (kolon depolama nitelikleri)
- `src/SchemaDiff.Core/Extraction/Sql.cs` — Columns: `is_sparse`, `is_filestream`,
  `is_rowguidcol`, `is_column_set`
- `src/SchemaDiff.Core/Extraction/{CatalogRows,CatalogExtractor}.cs` — `ColumnRow` alanları + mapper
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — kanonik nitelik token'ları + `ColumnInfo`
- `src/SchemaDiff.Core/Model/Snapshot.cs` — `ColumnInfo` nitelik alanları
- `src/SchemaDiff.Core/Scripting/TableScriptWriter.cs` — CREATE TABLE kolon nitelikleri
- `src/SchemaDiff.Core/Scripting/TableScriptGenerator.cs` — `AddColumnAttributes`,
  `AppendColumnAttributeDiff`
- `src/SchemaDiff.Core/Analysis/ChangeCatalog.cs` — kolon detayında nitelik değişimi
- Testler: `ColumnAttributeTests` (yeni)

## Dalga 6'da değişen dosyalar (tipli XML · NFR · index kilitleri)
- `src/SchemaDiff.Core/Extraction/Sql.cs` — `XmlSchemaCollections` (yeni sorgu),
  Columns/Indexes/ForeignKeys/CheckConstraints'e yeni alanlar
- `src/SchemaDiff.Core/Extraction/{CatalogRows,CatalogExtractor}.cs` — satır alanları + mapper'lar
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — koleksiyon id→ad haritası, kanonikler
- `src/SchemaDiff.Core/Model/Snapshot.cs` — `ColumnInfo.XmlTypeSuffix`, IndexDefinition kilitleri,
  Check/ForeignKeyDefinition `NotForReplication`
- `src/SchemaDiff.Core/Scripting/TableScriptWriter.cs` — XML tipi, IDENTITY/CHECK/FK NFR, WITH listesi
- `src/SchemaDiff.Core/Scripting/TableScriptGenerator.cs` — `WithOptions`, XML RenderType,
  ADD COLUMN IDENTITY, NFR imzaları
- Testler: `TypedXmlAndReplicationTests` (yeni)

## Dalga 7'de değişen dosyalar (constraint durumu)
- `src/SchemaDiff.Core/Model/Snapshot.cs` — Check/ForeignKeyDefinition `IsDisabled`/`IsNotTrusted`
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — tanımlara durum aktarımı
- `src/SchemaDiff.Core/Scripting/TableScriptGenerator.cs` — `AddClause`, `ConstraintState`,
  durum-farkı yolu (drop+recreate yerine tek ifade)
- `src/SchemaDiff.Core/Scripting/TableScriptWriter.cs` — `WriteConstraintState` (yeni tablo)
- Testler: `ConstraintStateTests` (yeni)

## Dalga 8'de değişen dosyalar (full-text)
- `src/SchemaDiff.Core/Extraction/Sql.cs` — `FullTextCatalogs`, `FullTextIndexes`, `FullTextIndexColumns`
- `src/SchemaDiff.Core/Extraction/{CatalogRows,CatalogExtractor}.cs` — satırlar + opsiyonel sorgular
- `src/SchemaDiff.Core/Model/ObjectKey.cs` — `ObjectKind.FullTextCatalog`
- `src/SchemaDiff.Core/Model/Snapshot.cs` — `FullTextIndexDefinition`, `FullTextIndexColumn`
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — katalog objeleri, `fullText` parçası
- `src/SchemaDiff.Core/Scripting/FullTextScript.cs` — **yeni**: CREATE/DROP/DISABLE (tek kaynak)
- `src/SchemaDiff.Core/Scripting/TableScriptWriter.cs` — yeni tabloda `WriteFullText`
- `src/SchemaDiff.Core/Scripting/TableScriptGenerator.cs` — `AppendFullTextDiff`
- `src/SchemaDiff.Core/Scripting/TypeScriptGenerator.cs` — katalog create/drop/exists
- Testler: `FullTextTests` (yeni, 22)

## Dalga 9'da değişen dosyalar (table type constraint'leri)
- `src/SchemaDiff.Core/Extraction/Sql.cs` — `TableTypeIndexes`, `TableTypeIndexColumns`,
  `TableTypeChecks`; `TableTypeColumns`'a DEFAULT
- `src/SchemaDiff.Core/Extraction/{CatalogRows,CatalogExtractor}.cs` — satırlar + opsiyonel sorgular
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — `BuildTableTypeConstraints`,
  `RenderTableType` gövde birleştirme
- Testler: `TableTypeConstraintTests` (yeni, 16)

## Dalga 10'da değişen dosyalar (XML / spatial index)
- `src/SchemaDiff.Core/Extraction/Sql.cs` — `Indexes`'ten type 3,4 dışlandı; `XmlIndexes`,
  `SpatialIndexes` eklendi
- `src/SchemaDiff.Core/Extraction/{CatalogRows,CatalogExtractor}.cs` — satırlar, `Rdr.NDbl`
- `src/SchemaDiff.Core/Model/Snapshot.cs` — `XmlIndexDefinition`, `SpatialIndexDefinition`
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — `xmlIndexes`/`spatialIndexes` parçaları
- `src/SchemaDiff.Core/Scripting/SpecialIndexScript.cs` — **yeni**: CREATE/DROP (tek kaynak)
- `src/SchemaDiff.Core/Scripting/{TableScriptWriter,TableScriptGenerator}.cs` — yeni tablo + diff
- Testler: `SpecialIndexTests` (yeni, 17)

## Dalga 12'de değişen dosyalar (index ek seçenekleri)
- `src/SchemaDiff.Core/Extraction/Sql.cs` — `IndexExtras` (yeni, opsiyonel)
- `src/SchemaDiff.Core/Extraction/{CatalogRows,CatalogExtractor}.cs` — `IndexExtraRow`
- `src/SchemaDiff.Core/Model/Snapshot.cs` — `IndexDefinition` iki yeni alan
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — okunamama koruması + kanonik
- `src/SchemaDiff.Core/Scripting/{TableScriptWriter,TableScriptGenerator}.cs` — WITH listesi
- Testler: `IndexExtraOptionTests` (yeni, 13)

## Dalga 13'te değişen dosyalar (XML schema collection)
- `src/SchemaDiff.Core/Extraction/Sql.cs` — `XmlSchemaNamespaces`, `XmlSchemaCollectionContent`
- `src/SchemaDiff.Core/Extraction/{CatalogRows,CatalogExtractor}.cs` — satırlar + opsiyonel sorgular
- `src/SchemaDiff.Core/Model/{ObjectKey,ObjectKindLabels}.cs` — yeni tür + etiketi
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — koleksiyon objeleri (namespace + content)
- `src/SchemaDiff.Core/Scripting/TypeScriptGenerator.cs` — CREATE/DROP/exists, öncelik -2
- Testler: `XmlSchemaCollectionTests` (yeni, 16)

## Dalga 1–3'te değişen ana dosyalar (referans)
- `src/SchemaDiff.Core/Analysis/CoverageProbe.cs` — sayaçlar (Probes internal)
- `src/SchemaDiff.Core/Extraction/Sql.cs` — Indexes (data_compression), Columns (generated_always/is_hidden)
- `src/SchemaDiff.Core/Extraction/CatalogRows.cs` — IndexRow.DataCompression, ColumnRow.GeneratedAlwaysType/IsHidden
- `src/SchemaDiff.Core/Extraction/SnapshotBuilder.cs` — imzalar + temporal writer beslemesi
- `src/SchemaDiff.Core/Scripting/TableScriptWriter.cs` — temporal CREATE (PERIOD + SYSTEM_VERSIONING)
- `src/SchemaDiff.Core/Scripting/TableScriptGenerator.cs` — index compression, AppendTemporalDiff
- `src/SchemaDiff.Web/{Contracts,CompareService}.cs` + `wwwroot/app.js` — IgnoreDataCompression ayarı
- Testler: `CoverageProbeTests`, `TableIndexConstraintTests` (compression), `TemporalTests` (script)

## Güvenlik kısıtları (değişmez)
- Salt-okunur: yalnız katalog/DMV `SELECT`. Web yalnız `127.0.0.1`.
- `jobs.json` bağlantı dizeleri içerir → **asla commit'lenmez** (gitignore'da).
- Parola/token/PAT sohbete yazılmaz.
