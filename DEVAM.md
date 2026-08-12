# DEVAM — Nerede Kaldık (kapsam genişletme çalışması)

> Bu dosya, başka bir makinede kaldığın yerden devam edebilmen için yazıldı.
> Son güncelleme: 2026-08-12

## Hızlı durum
- **Repo:** `smailyy13/databasedeployer` (GitHub, private) — proje adı **SchemaDiff**
- **Branch:** `main` — güncel commit **`3b12de5`** (push edilmiş, `main == origin/main`)
- **Testler:** **276 yeşil**
- **Son release:** **v1.4** (portable, 4 parça). **v1.5 HENÜZ ÇIKARILMADI** — aşağıya bak.

## Başka makinede devam etmek için
```bash
git clone https://github.com/smailyy13/databasedeployer.git
cd databasedeployer            # (klasör adı repo adı)
git pull                        # en güncel main

# Derle + test
dotnet build -c Release
dotnet test  -c Release --no-build      # 276 test geçmeli

# Web arayüzünü çalıştır (yerel, sadece 127.0.0.1)
dotnet run -c Release --project src/SchemaDiff.Web -- --port 5290
# → tarayıcıda http://127.0.0.1:5290
```
> Not: `jobs.json` **gitignore'da** (bağlantı dizeleri içerir, repoda YOK). Yeni makinede
> kendi bağlantılarını arayüzden gireceksin.

## Bu oturumda tamamlananlar (hepsi commit + push)
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

## KALDIĞIMIZ KARAR NOKTASI  ← buradan devam
En yüksek değerli 3 dalga bitti. Kullanıcıya 3 seçenek sunuldu, **cevap bekleniyor:**

- **(A)** Dur; kalanlar tespit-only kalsın (CoverageProbe zaten sayıyor). **v1.5 release** çıkar.
- **(B)** **Kullanıcı istatistikleri** (`CREATE STATISTICS`) scriptlemeyi ekle → sonra release.
- **(C)** *(önerilen)* Önce şirket DB'nde **CoverageProbe'u çalıştır**, gerçekten sayısı > 0
  çıkan sınıfları gör, yalnızca onları scriptle. (Boşa iş yapmamak için en verimlisi.)

### Kalan scriptlenebilir maddeler (düşük öncelik)
- Kullanıcı istatistikleri (`CREATE STATISTICS`) — yeni obje sınıfı, DDL trigger gibi
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

## Değişen ana dosyalar (referans)
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
