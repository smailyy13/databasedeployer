# DEVAM — Nerede Kaldık (kapsam genişletme çalışması)

> Bu dosya, başka bir makinede kaldığın yerden devam edebilmen için yazıldı.
> Son güncelleme: 2026-08-12 (Dalga 4 sonrası)

## Hızlı durum
- **Repo:** `smailyy13/databasedeployer` (GitHub, private) — proje adı **SchemaDiff**
- **Branch:** `main`
- **Testler:** **536 (533 yeşil + 3 atlanan entegrasyon)**
- **Son release:** **v1.4** (portable, 4 parça). **v1.5 HENÜZ ÇIKARILMADI** — aşağıya bak.
- **Gereken SDK:** .NET 10 (`dotnet-install.sh --channel 10.0`; macOS'ta `~/.dotnet`).

## Başka makinede devam etmek için
```bash
git clone https://github.com/smailyy13/databasedeployer.git
cd databasedeployer            # (klasör adı repo adı)
git pull                        # en güncel main

# Derle + test
dotnet build -c Release
dotnet test  -c Release --no-build      # 533 geçer, 3 atlanır (entegrasyon, canlı SQL ister)

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

## Bu oturumda tamamlananlar (Dalga 4 — henüz COMMIT EDİLMEDİ)

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

7. **Test:** 276 → **558** (555 yeşil + 3 atlanan entegrasyon).
   - `StatisticsTests` (33): karşılaştırma, sıra, filtre/NORECOMPUTE/INCREMENTAL,
     drop-önce/create-sonra sıralaması, yeni tablo script'i, okunamayan sorgu davranışı.
   - `CatalogQueryTests` (yeni): TÜM katalog sorgularının yapısal denetimi — en önemlisi
     **salt-okunurluk** (yazan bir ifade sızarsa yalnız bu test yakalar), ayrıca
     `SELECT *` yasağı, parantez dengesi, yalnız `sys.*` okuma, `is_ms_shipped = 0` filtresi.
     Denetçinin kendisi de test ediliyor (bilerek bozuk SQL'i yakalıyor mu).
   - `ColumnAttributeTests` (22): nitelik farkı, CREATE/ADD COLUMN yazımı, gramer sırası
     (FILESTREAM → SPARSE), aç/kapa ifadeleri, tip değişimiyle birlikte davranış,
     FILESTREAM/COLUMN_SET'in script yerine uyarıya düşmesi.

### Sıradaki adım
Commit + push, sonra **v1.5 release** (aşağıdaki publish adımları).

### Kalan scriptlenebilir maddeler (düşük öncelik)
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
