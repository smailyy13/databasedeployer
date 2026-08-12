# Durum ve Yol Haritası

Son güncelleme: 2026-08-12 · 5 proje (Core, Cli, Web, Bench, Tests) · 846 test (843 yeşil + 3 atlanan entegrasyon)

---

## ✅ Tamamlanan ve doğrulanan

### Karşılaştırma motoru

| Yetenek | Durum | Doğrulama |
|---|---|---|
| Toplu katalog çekimi (15 paralel sorgu) | ✅ | 10.000 objede ~1.050 ms |
| Kanonikleştirme + XxHash128 karşılaştırma | ✅ | Karşılaştırma adımı 13 ms |
| ScriptDom token normalizasyonu | ✅ | Yalnızca biçim/yorum farkı olan obje "aynı" sayılıyor |
| Sistem üretimi constraint adlarını bastırma | ✅ | `DF__Tbl__A1B2` gürültüsü yok |
| Yetki eksikliğini tespit (`VIEW DEFINITION`) | ✅ | Sessiz "aynı" yerine "Belirsiz" raporlanıyor |
| Eşzamanlı sorgu sınırı (bağlantı boğulmasını önler) | ✅ | 7 katman × 18 sorgu × 2 taraf senaryosu |

**Hız:** DacFx (SSDT'nin motoru) 33 sn ↔ SchemaDiff 1,4 sn → **22,8x**, aynı 359 fark.

### Kapsanan obje sınıfları

Şemalar · Tablolar (kolonlar, computed, identity, default'lar, collation) · Index'ler
(key/include/filter/columnstore) · PK ve UNIQUE · CHECK · FOREIGN KEY · View'lar ·
Prosedürler · Skaler ve tablo fonksiyonları · DML trigger'ları (etkin/pasif dahil) ·
Sequence'lar · Synonym'ler · Extended property'ler (obje/kolon/şema) · Kullanıcı tanımlı
roller ve üyelikleri · Obje/şema izinleri (GRANT/DENY) · Kullanıcı tanımlı alias tipler ·
Table type'lar (kolon yapısı) · Partition function ve scheme'ler · Temporal (system-versioned)
tablolar (history hariç) · DDL trigger'ları · Veritabanı seviyesi izinler ve extended
property'ler (sentetik "(database)" objesi) · Yaklaşık satır sayıları

### Analiz

| Yetenek | Durum |
|---|---|
| Add / Change / Delete kategorizasyonu | ✅ |
| Alt kırılım: Columns, Primary Key, Indexes, Foreign Keys, Check Constraints, Properties | ✅ |
| Eklenen/silinen objenin içeriğini dökme | ✅ |
| Deployment risk sınıflandırması (Safe / InPlace / BlockedIfNotEmpty / DataLoss) | ✅ |
| Hedef satır sayısıyla birleştirme — "gerçekten duracak mı?" | ✅ |
| Trigger durum farkı raporu | ✅ |

Risk analizi SSDT'nin `data loss could occur` uyarılarıyla birebir örtüştü (49 = 49).

### Arayüzler

**Web** — SSDT Schema Compare düzeni: kaynak ⇄ hedef seçicileri, SSDT'nin Connect
diyaloğuyla aynı alanlar, son bağlantılar (parola Windows DPAPI ile), veritabanı
listeleme, 10 karşılaştırma seçeneği, iç içe sonuç ağacı (işaret kutusu + eylem
simgesi), alt panelde T-SQL renklendirmeli tam script diff'i, fark blokları arası
gezinme, risk ve trigger görünümleri.

**CLI** — JSON iş listesi, etkileşimli/argümanla seçim, paralel koşum, sonuçlar
bittikçe akar, CI'a uygun çıkış kodları.

**Bench** — DacFx ile head-to-head ölçüm.

---

## ⚠️ Kısmi

| Konu | Ne var | Ne yok |
|---|---|---|
| Deployment script'i | Tip/sequence/synonym + tablolar (kolon + DEFAULT + index/PK/UQ/CHECK/FK) + modüller + roller + izinler + EP | Partition/temporal DDL, kolon sırası (tespit+uyarı var, üretim yok), veri taşıma |
| Tablo değişikliği | Kolon ekle/genişlet/daralt/sil + index/constraint değişikliği | Kolon SIRASI (ortaya ekleme → tablo yeniden oluşturma) |
| İşaret kutuları | Seçim → yalnızca seçilenlerden script üretme (web'de bağlı ✅) | CLI'de per-obje seçimli script |
| Çok veritabanı | CLI'de tam | Web'de tek çift |
| Ölçüm | Yerel fixture'larda uçtan uca doğrulandı | Gerçek dev/prod ortamında hiç koşulmadı |

---

## ❌ Eksik

### 1. Deployment script üretimi — modüller ✅, tablolar ⚠️ (kolon dilimi ✅)

**Modüller tamamlandı** (view, prosedür, fonksiyon, trigger + gerekli şemalar):

- `CREATE` / `ALTER` / `DROP` doğru fiillerle, `OBJECT_ID` koruması ile
- `CREATE`→`ALTER` dönüşümü ScriptDom **token** düzeyinde (yorumdaki "create" bozulmaz)
- Yeni objeler `sys.sql_expression_dependencies` ile topolojik sırada; döngü varsa uyarır
- Her modülün özgün `ANSI_NULLS` / `QUOTED_IDENTIFIER` ayarı korunur
- `XACT_ABORT` + tek transaction
- Kapsam dışı kalan her obje script başlığında **ismen listelenir**

**Tablolar — kolon seviyesi dilimi tamamlandı** (`TableScriptGenerator`):

- Yeni tablo → `CREATE` (snapshot display script'inden)
- Değişen tablo → kolon `ADD` / `ALTER COLUMN` / `DROP COLUMN`
- **Güvenlik kademesi:** additive değişiklikler (nullable kolon, tip genişletme)
  doğrudan script'e girer; veri kaybı riski taşıyanlar (kolon/tablo silme, tip daraltma,
  dolu tabloya NOT NULL kolon) `--allow-data-loss` olmadıkça script'e GİRMEZ ve
  `DataLossActions` içinde ismen raporlanır — sessizce düşmez
- IDENTITY/computed değişimi ALTER COLUMN ile yapılamaz → atlanır, sebebi bildirilir
- Modül üreteci ile birleşik çıktı: tablolar önce (view'lar onlara bağlı olabilir)

Birleşik script CLI (`--script`, `--script-scope`, `--allow-data-loss`) ve web
(`/api/runs/{id}/script?scope=&dataLoss=`) üzerinden üretilir.

Uçtan uca doğrulandı (yerel fixture, gerçek SQL Server):
script üretildi → veritabanına uygulandı → yeniden karşılaştırıldı → fixture geri yüklendi.

| Test | Önce | Script sonrası | Kalan |
|---|---|---|---|
| Küçük fixture (Dev→Prod) | 5 fark | **1** | kolon sırası + index INCLUDE (kapsam dışı, doğru) |

**Kalan: index/constraint değişikliği üretimi ve veri taşıma.** Altın referans elimizde:
SSDT'nin ürettiği `SchemaDiff_Big2_Update1.publish.sql`.

### 2. Kapsam durumu (güncel: 2026-07-25)

**Kapsanan (karşılaştırılıyor):** şemalar · tablolar (kolon/computed/identity/default/
collation) · index (key/include/filter/columnstore) · PK/UQ/CHECK/FK · view/prosedür/
fonksiyon · DML trigger · sequence · synonym · obje/kolon/şema/**db**/parametre/principal/index extended property ·
roller + üyelikleri · obje/şema/**db** izinleri (GRANT/DENY) · alias tipler · table type
(kolon + constraint/index) · partition function/scheme · **temporal (system-versioned)** · **DDL trigger** ·
**kullanıcı istatistikleri (CREATE STATISTICS)** · **kolon depolama nitelikleri
(SPARSE / FILESTREAM / ROWGUIDCOL / COLUMN_SET)** · **tipli XML kolonları
(xml(CONTENT/DOCUMENT koleksiyon))** · **NOT FOR REPLICATION (IDENTITY / CHECK / FK)** ·
**index kilit seçenekleri (ALLOW_ROW_LOCKS / ALLOW_PAGE_LOCKS)** · **constraint durumu
(pasif / güvenilmez) — script'e de yansıyor** · **full-text katalog + index** · **table type constraint/index/DEFAULT'ları** · **XML ve spatial index'ler** · **index ek seçenekleri (STATISTICS_NORECOMPUTE / OPTIMIZE_FOR_SEQUENTIAL_KEY)** ·
**XML schema collection** · **full-text stoplist** · **plan guide** · **database scoped configuration** · **legacy RULE/DEFAULT**.

**Hâlâ kapsanmayan** (sessizce atlanır — sayısı >0 çıkarsa `--coverage` uyarır):

- Veritabanı kullanıcıları (ortama özgü) ve sabit rol üyelikleri (db_datareader vb.)
- In-memory OLTP tabloları · Graph node/edge tablolar
- CLR: assembly, tip, CLR prosedür/fonksiyon
- CLR tipleri
- Selective XML index (otomatik/index istatistikleri
  bilinçli kapsam dışı: şema değil, optimizer artefaktı)
- Always Encrypted · Row-Level Security · sertifika/anahtar/credential
- Service Broker · PolyBase · application role
- PRIMARY dışı filegroup'lar

### 3. Otomatik test — ✅ kuruldu (846 test: 843 birim + 3 entegrasyon)

Entegrasyon testleri (`IntegrationTests`, `[SkippableFact]`) canlı SQL Server'a karşı koşar,
DB yoksa ATLANIR (CI'da derleme yeşil kalır): küçük fixture 5 fark (2/1/2), Big 359 fark,
ve Big script'inin SSDT operasyon sayılarıyla uyumu (125 ALTER PROC, 25 DROP PROC, 10 CREATE
TABLE, 29 DROP INDEX) regresyona kilitlenir.

Ek analiz: **rename algılama** (yapısı birebir aynı drop+add çifti → sp_rename önerisi),
**dış referans uyarısı** (cross-db/linked server), **bracket/tırnak normalizasyonu**
(`[a]`=`a`), **rollback script** (`--rollback`, ters yön), **cross-table FK sırası**
(tüm FK drop başta, add sonda).

### 3b. (eski not) İlk test projesi

`test/SchemaDiff.Tests` (xUnit). Saf mantık DB'siz izole test ediliyor:

- `TSqlNormalizer` — boşluk/yorum sadeleştirme, string içi `--` yanılgısı, keyword casing
- `ModuleScriptGenerator.ToAlter` — token düzeyi CREATE→ALTER, yorumdaki "create" korunur
- `SchemaComparer` — Added/Removed/Changed/Equal/Indeterminate yönü, alt-parça farkı
- `ObjectKey` / `ObjectKeyComparer` — case duyarlılığı, sys type eşleme
- `DeploymentRiskAnalyzer` — kolon ekle/sil/genişlet/daralt, NOT NULL, WillBlock
- `ModuleScriptGenerator` — CREATE/ALTER/DROP, topolojik sıra, trigger atlama, seçim
- `TableScriptGenerator` — additive vs veri kaybı kademesi, IDENTITY atlama, gating

Kalan: entegrasyon testleri (canlı DB'ye karşı), altın referans (SSDT publish) regresyonu.

### 4. Diğerleri

- Snapshot cache (prod tarafı nadiren değişir; ikinci karşılaştırma milisaniyelere iner)
- `.dacpac` / `.sqlproj` kaynak desteği (bugün yalnızca canlı DB ↔ canlı DB)
- Kimlik doğrulama (araç yalnızca `127.0.0.1`'e bağlı; ağa açmak için şart)
- Kontrol tablosu veri karşılaştırması (deployment prosedürü adım 6-7'deki
  `ParallelRun` `EXCEPT` sorguları — aynı akışın parçası)

---

## Önerilen sıra

**1. Gerçek ortamda koş** — 🔧 *alet hazır, koşulmayı bekliyor*

`tools/adim1-olcum.ps1` her katman için kapsam sondası + gerçek karşılaştırma koşar ve
tek dosyaya yazar. Taşınabilir sürüm (`dotnet publish -r win-x64 --self-contained`)
hedef makinede .NET kurulumu gerektirmez.

```powershell
.\adim1-olcum.ps1                       # 7 katmanın tamamı
.\adim1-olcum.ps1 -Databases EDWSTG     # tek katman
```

**Kapsam sondası** (`--coverage`) her obje sınıfının veritabanında kaç tane
bulunduğunu sayar ve hangilerinin karşılaştırma dışında kaldığını listeler. Bu,
kapsam eksiğini tahminden çıkarıp veriye bağlar.

Salt okunurdur: yalnızca katalog view'larına SELECT atar, veri okumaz, hiçbir şey
değiştirmez. Çıktıda kimlik bilgisi ya da veri bulunmaz — obje adları, sayılar, süreler.

**2. Test projesi kur** — ✅ *yapıldı* (78 birim testi, xUnit)
Kalan: canlı DB entegrasyon testleri ve SSDT publish altın referans regresyonu.

**3. Kapsamı tamamla** (1-2 hafta, adım 1'in çıktısına göre)
Extended property'ler ✅, roller + izinler ✅, alias tipler + table type'lar ✅.
Sıradaki adaylar: partition function/scheme, full-text, DDL trigger'ları, veritabanı
seviyesi izinler — hangilerinin gerçek EDW'de çıktığını adım 1 gösterecek.

**4. Deployment script üretimi** — modüller ✅, additive tablolar ✅, kalan ⚠️
Modüller ve kolon seviyesi tablo değişiklikleri tamamlandı (güvenlik kademesiyle).
Kalan: index/constraint değişikliği üretimi, kolon sırası için tablo yeniden oluşturma,
veri taşıma. Veri kaybı adımları zaten ayrı onaya (`--allow-data-loss`) bağlı.

**5. Snapshot cache ve çok veritabanlı web arayüzü**
