# SchemaDiff

📄 [Kullanım Kılavuzu (PDF)](docs/SchemaDiff-Kullanim-Kilavuzu.pdf)

SQL Server için hızlı bir **şema karşılaştırma + dağıtım (deployment) aracı**. SSDT /
Visual Studio "Schema Compare" ile aynı soruyu cevaplar — *iki veritabanı arasında ne
değişti?* — ama büyük veritabanlarında dakikalar yerine **saniyeler** sürer; üstüne
üretilen dağıtım script'ini doğrudan veritabanında **SSMS tarzı** çalıştırabilirsiniz.

İki arayüz, tek motor:

- **Web arayüzü** (`SchemaDiff.Web`) — SSDT Schema Compare'e benzeyen tarayıcı arayüzü:
  bağlan → karşılaştır → script üret → **veritabanında çalıştır** (canlı Results/Messages).
- **CLI** (`SchemaDiff.Cli`) — toplu/otomasyon: çok sayıda katmanı paralel karşılaştır,
  risk analizi, trigger durum raporu, script dosyası üret.

> **Durum: finalize edildi.** Karşılaştırma, raporlama, risk analizi, dağıtım script'i
> üretimi ve script'i veritabanında çalıştırma çalışır. 1.100+ birim testi yeşil.

---

## İçindekiler

- [Kurulum](#kurulum)
- [Hızlı başlangıç (web)](#hızlı-başlangıç-web)
- [Neden daha hızlı?](#neden-daha-hızlı)
- [Web arayüzü](#web-arayüzü)
  - [Karşılaştırma + Object Definitions](#karşılaştırma--object-definitions)
  - [Favoriler](#favoriler)
  - [Dağıtım script'i üretimi](#dağıtım-scripti-üretimi-web)
  - [Veritabanında çalıştır (SSMS tarzı)](#veritabanında-çalıştır-ssms-tarzı)
  - [Bellek kullanımı](#bellek-kullanımı)
  - [Parola saklama](#parola-saklama)
- [Karşılaştırma seçenekleri](#karşılaştırma-seçenekleri)
- [Dağıtım script'i: kurallar ve sıralama](#dağıtım-scripti-kurallar-ve-sıralama)
- [CLI kullanımı](#cli-kullanımı)
- [Risk analizi ve trigger raporu](#risk-analizi-ve-trigger-raporu)
- [Kapsam](#kapsam)
- [Testler](#testler)
- [Branch'ler ve .NET sürümleri](#branchler-ve-net-sürümleri)
- [Yapı](#yapı)
- [Güvenlik notu](#güvenlik-notu)

---

## Kurulum

Üç yol var. **Çoğu kullanıcı için en kolayı 1. yoldur** — .NET kurmaya gerek yoktur.

### 1) Hazır paket (self-contained exe — .NET kurulumu gerekmez)

GitHub **Releases** sayfasından `SchemaDiff.zip`'i indir, bir klasöre çıkar ve çalıştır:

```powershell
# zip'i aç, sonra:
.\SchemaDiff.Web.exe
```

Tarayıcıda **http://127.0.0.1:5290** aç. **Internet Explorer DEĞİL** — Edge, Chrome ya da
Firefox kullan (arayüz modern CSS/JS kullanır, IE desteklemez).

- Paket **self-contained**'dir: .NET çalışma zamanı exe'nin içinde gelir, hedef makineye
  hiçbir şey kurmanıza gerek yoktur (yönetici hakkı da gerekmez).
- İçinde `wwwroot/` klasörü diskten servis edilir; sadece arayüzü güncellemek isterseniz
  (yeni sürümde) o üç dosyayı (`app.js`, `index.html`, `styles.css`) değiştirip tarayıcıda
  **Ctrl+F5** yapmak yeter — exe'yi yeniden indirmeniz gerekmez.

**Gereksinimler (hedef makine):** Windows x64; erişilebilen bir SQL Server (yerel ya da
ağ). Araç yalnızca `127.0.0.1`'i dinler, yani sunucuya uzaktan erişim açılmaz — arayüze
aynı makineden bağlanırsınız, karşılaştırılan SQL Server uzak olabilir.

### 2) Kaynaktan çalıştır (geliştirici)

[.NET SDK](https://dotnet.microsoft.com/download) gerekir (`main`/`main_clean` için **.NET 10**,
`main_clean_8` için **.NET 8**).

```powershell
git clone https://github.com/smailyy13/databasedeployer.git
cd databasedeployer
dotnet run --project src\SchemaDiff.Web -c Release
# → http://127.0.0.1:5290   (port doluysa: -- --port 5300)
```

### 3) Kendi self-contained paketini üret (dağıtım için)

Tek dosyalık, bağımsız bir exe üretmek için:

```powershell
dotnet publish src\SchemaDiff.Web\SchemaDiff.Web.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -o yayin
```

`yayin\` klasörünü zip'leyip paylaşabilirsiniz (~45 MB zip / ~110 MB exe — çalışma zamanı
dahil). `main_clean_8` (net8) paketi, net8 çalışma zamanı olmayan makinelerde de çalışır.

> **Yalnızca net10 çalışma zamanı kurulu bir makinede net8 çıktısını koşturmak** için
> roll-forward açın: `$env:DOTNET_ROLL_FORWARD = "LatestMajor"`.

---

## Hızlı başlangıç (web)

1. `SchemaDiff.Web.exe`'yi çalıştır → tarayıcıda `http://127.0.0.1:5290`.
2. Üstte **SOURCE** (kaynak/dev) ve **TARGET** (hedef/prod) bağlantılarını seç
   (sunucu, kimlik doğrulama, veritabanı; veritabanı listesi sunucudan gelir).
3. **Compare** → farklar `Add / Change / Delete` olarak gruplanır.
4. **Generate Script** → üretilen T-SQL (renkli, düzenlenebilir).
5. **Run on database** → onay popup'ı → script hedef veritabanında çalışır, sonuçlar
   SSMS'teki gibi **Results** (grid) ve **Messages** sekmelerinde canlı akar.

İsterseniz kaynak+hedef çiftini **★ favorilere** kaydedip bir sonraki açılışta tek tıkla
tüm ayarlarıyla geri yükleyebilirsiniz.

---

## Neden daha hızlı?

SSDT'nin altındaki DacFx, karşılaştırmadan önce her iki taraf için de tam bir
**semantik model** kurar: her objenin DDL'ini parse eder, isim çözümlemesi yapar,
bağımlılık grafiğini çıkarır, doğrulama çalıştırır. Bu obje başına pahalıdır ve
"ne değişti?" sorusu için gereksizdir.

Bu araç bunun yerine:

1. **Toplu katalog çekimi** — obje başına sorgu yok, obje *sınıfı* başına tek sorgu.
   Sorgular paralel bağlantılarda koşar (`Sql.cs`); çoğu opsiyoneldir, yani eski bir
   SQL Server sürümünde ya da yetki eksikliğinde düşen bir sorgu karşılaştırmayı durdurmaz.
2. **Kanonikleştirme** — modül gövdeleri ScriptDom'un *token akışıyla* normalize edilir
   (regex ile değil; regex string literal içindeki `--` ile yanılır).
3. **Hash karşılaştırması** — her obje ve alt-parçası için XxHash128. Eşit hash'li
   objenin metnine hiç bakılmaz. Dev↔prod arasında objelerin %95+'i eşit olduğundan
   pahalı iş yalnızca kalan azınlığa uygulanır.
4. **Alt-parça hash'leri** — bir obje tek hash değil, parça parça hash'lenir (`columns`,
   `indexes`, `checks`, `foreignKeys`, `statistics`, `body`, `permissions`…). Böylece
   "bu tabloda yalnız index değişmiş" cevabı metin diff'i çalıştırmadan elde edilir ve
   script üreteci yalnızca değişen parçayı işler.

Ölçüm (10.000 objeli iki veritabanı, 255 gerçek fark, yerel SQL Server): **SchemaDiff
~1.3 sn**, DacFx ~33 sn — **~24x hızlı**, aynı fark kümesi. (Benchmark `src/SchemaDiff.Bench`
ile alınır; kapsam o ölçümden beri büyüdüğü için güncel oranı kendiniz ölçebilirsiniz.)

---

## Web arayüzü

Düzen bilinçli olarak SSDT Schema Compare'e benzetildi. Arayüz tam **EN/TR** (sağ üstteki
dil düğmesi), açık/koyu tema.

### Karşılaştırma + Object Definitions

- Üstte **kaynak (dev) ⇄ hedef (prod)** bağlantı seçicileri ve **⇄** ile takas.
- Bağlantı diyaloğu SSDT'nin *Connect* penceresiyle aynı alanları sorar: sunucu adı,
  kimlik doğrulama (Windows / SQL Login), kullanıcı/parola, veritabanı (sunucudan
  listelenir), Encrypt, Trust Server Certificate — ham bağlantı dizesi yazmazsınız.
- **Son bağlantılar** (SSDT'deki *Recent Connections*) yeniden seçim için saklanır.
- Sonuçlar **Delete / Change / Add** olarak gruplanır, tür sütunuyla (Table, View,
  Procedure, Trigger…); değişen objeler açılıp **Add Column / Alter Column / Delete Index**
  gibi alt değişikliklere inilir.
- Altta **Object Definitions** paneli: seçili objenin **tam metni** iki taraflı, gerçek
  satır numaralarıyla. Fark hizalaması **Myers diff** ile yapılır — büyük bir prosedürde
  baştan/ortadan tek satır kayması tüm gövdeyi "değişti" göstermez; yalnız gerçekten
  değişen satırlar renklenir. Boşluk/girinti/sondaki virgül farkları hizalamada yok sayılır.
- ▲ ▼ tuşları fark blokları arasında gezinir (`3 / 12 differences`).
- "canonical text" kutusu karşılaştırmanın gerçekte neyi hash'lediğini gösterir.
- **Deployment risk** ayrı bir görünüm: renkli özet çipleri, ciddiyete göre sıralı ve
  tıklanabilir kartlar.

### Favoriler

Üst bardaki **★** ile mevcut kaynak+hedef bağlantı çiftini isimle kaydedersiniz; panelden
bir favoriye tıklayınca iki bağlantı da (sunucu, veritabanı, auth, kullanıcı, encrypt/trust)
ve karşılaştırma seçenekleri geri yüklenir. Favoriler cihazda (tarayıcıda) saklanır.
**Parola favoriye yazılmaz** — SQL auth'ta "parolayı hatırla" ile sunucuda tutulan parola
otomatik çözülür, yoksa bir kez sorulur.

### Dağıtım script'i üretimi (web)

Karşılaştırmadan sonra **Generate Script**. Sonuç ağacındaki işaret kutularıyla **yalnızca
seçtiğiniz objeleri** yazdırabilirsiniz — bir alt öğeyi (kolon/izin) işaretlemek o objeyi de
script'e alır; hiçbir şey seçmezseniz tümü yazılır. Script renkli, **düzenlenebilir** bir
editörde açılır; **Copy** / **Download .sql** ile dışarı alınabilir. Çok büyük script'ler
(>2 MB) tarayıcıyı kilitlememek için editöre yüklenmez, yalnız indir/kopyala sunulur.

Bir objeyi **⇄** ile işaretlemek onu **ters yönde** (hedef → kaynak) yazar — "şu üç objede
prod doğru, gerisinde dev doğru" durumu tek dosyada ifade edilebilir. Script editörünün
üstünde **source → target** ve **target → source** sekmeleri ayrı ayrı ele alınır.

### Veritabanında çalıştır (SSMS tarzı)

Script editöründe **Run on database** düğmesi. Ortada bir **onay popup'ı** çıkar — hangi
yönde (kaynak → hedef), hangi hedef veritabanı üzerinde ve kaç batch çalışacağını gösterir;
onaylayınca script hedefte çalışır. Çıktı **SSMS'e birebir yakındır**:

- **Results** sekmesi: her `SELECT` sonucu için bir **grid** (satır başlıkları, NULL italik
  gri, kolon başlıkları). Sonuç geldiğinde otomatik bu sekmeye geçilir.
- **Messages** sekmesi: `PRINT` çıktısı, `(N rows affected)`, sonda `Completion time`;
  **yalnız hatalar kırmızı** — `Msg N, Level L, State S, Line L` + mesaj (SSMS formatı).
- **Stop** düğmesi: çalışan deploy'u **yarıda durdurabilirsiniz** — açık SQL batch'i anında
  iptal edilir, açık transaction geri alınır.
- Script `GO` satırlarından batch'lere bölünüp **tek bağlantıda** sırayla koşturulur, yani
  script'in kendi `BEGIN TRAN … COMMIT` transaction'ı batch'ler boyunca geçerli kalır.

> ⚠️ Deploy **gerçekten** hedef veritabanını değiştirir; geri alma yoktur (ters script'le
> geri alabilirsiniz). Script çok-dilimli olduğundan, bir hata/durdurma açık transaction'ı
> geri alır ama **önceki commit'li dilimler** (ör. en baştaki şema oluşturma) kalmış olabilir.

### Bellek kullanımı

Bir karşılaştırma oturumu İKİ tam şema snapshot'ı tutar. Oturum **sayısı sınırlanmaz**;
oturum, ait olduğu **sekme kapanınca** (ya da sayfa yenilenince / aynı sekmede yeni
karşılaştırma başlayınca) silinir. Tarayıcı çökerse tek güvenlik ağı sekmenin dakikada bir
gönderdiği `/touch` sinyalidir: **30 dakika** haber alınmayan oturum bir sonraki
karşılaştırmada düşürülür. Web sunucusu tek-kullanıcılı yerel araç olduğundan bilinçli olarak
**Workstation GC** ile koşar (daha düşük RSS).

### Parola saklama

"Parolayı hatırla" işaretlenirse parola **Windows DPAPI** ile, oturum açan kullanıcı
hesabına bağlı şifrelenip `%APPDATA%\SchemaDiff\recent.json`'a yazılır. Başka bir hesap ya
da başka makine dosyayı okusa bile çözemez. Parola tarayıcıya **hiçbir yanıtta** geri
gönderilmez; kayıtlı bağlantı seçilince sunucu tarafında çözülür. Windows dışında (DPAPI yok)
parola hiç saklanmaz. Güvenlik modeli SSMS'in "parolayı hatırla"sıyla aynı seviyededir.

---

## Karşılaştırma seçenekleri

**Ayarlar** (⚙️) menüsü açılır/kapanır kategoriler hâlinde; her seçenek tek satır, açıklaması
hover ipucunda. Hepsi gerçekten karşılaştırmaya uygulanır (her biri davranışsal test altında).

| Grup | Seçenekler |
|---|---|
| **Metin / biçim** | Boşlukları yok say · Yorumları yok say · Noktalı virgülleri yok say · ANSI NULLS · Quoted identifier |
| **Harf duyarlılığı** | Nesne adları · Kolon adları · Anahtar kelimeler |
| **Kolon** | Kolon sırasını yok say · Collation'ı yok say · Identity seed · Identity increment |
| **Index** | Fiziksel seçenekler · Fill factor · Index padding · Data compression · İstatistikler |
| **Nesneler** | DML trigger durumu · **DDL trigger'ları yok say** · System-named constraint'ler |
| **Kapsam** | Extended property'ler · İzinler |
| **Davranış** | Başlangıçta tüm değişiklikleri seçili getir |

Üretilen tablo script'i, karşılaştırmada yok sayılan şeyleri (sistem üretimi constraint
adları, collation vb.) **yazmaz da** — ekranda görünmeyen fark script'e de girmez.

---

## Dağıtım script'i: kurallar ve sıralama

Üretilen SQL bilinçli olarak sadedir: `USE` + transaction (`SET XACT_ABORT ON`) + DDL.
Başlık yorumu/`PRINT` ilerleme satırı yoktur. Dilimler **doğru bağımlılık sırasında** yazılır:

1. **FK drop'ları en başta** (kolon/index/tablo değişikliklerini engellemesinler).
2. **Şemalar** → **tipler/tablo öncesi objeler** (XML schema collection, partition
   function/scheme, alias tip, table type, sequence, synonym, full-text, legacy RULE/DEFAULT).
3. **Tablolar** — bağımlılık (FK) grafiğine göre topolojik sırada `CREATE`; değişen
   tablolarda sıra: **index/constraint DROP → kolon DROP → kolon ALTER → kolon ADD →
   index/constraint ADD**.
4. **Modüller** (view/prosedür/fonksiyon/trigger) — `sys.sql_expression_dependencies` ile
   topolojik sırada; `CREATE`↔`ALTER` dönüşümü ScriptDom token düzeyinde; her modülün
   `ANSI_NULLS`/`QUOTED_IDENTIFIER`'ı korunur.
5. **Roller → izinler/extended property → ayarlar/plan guide** (en sonda).
6. **FK add'leri en sonda** (referans edilen tablolar/kolonlar artık var).
7. **Tablo drop'ları** ters topolojik sırada.

Doğru uygulanan incelikler:

- **Depolama yerleşimi** `ON [filegroup]` / `ON [scheme]([kolon])` — tablo, index, PK/UNIQUE
  ve **clustered/nonclustered columnstore** için üretilir. Partition'lı bir clustered
  columnstore bile doğru yazılır:
  `CREATE CLUSTERED COLUMNSTORE INDEX [...] ON [...] ON [sch]([Dt]);`
- **Değişmeyen index'teki kolonu ALTER COLUMN** — SQL Server bunu reddeder (Msg 5074);
  üreteç o index'i ALTER'dan önce düşürüp sonra aynı tanımla yeniden kurar (standalone
  index + PK/UNIQUE constraint dahil).
- **Kolon adı harf durumu** (`CustomerID` → `CustomerId`) `sp_rename` ile, ADD/DROP üretmeden.
- **Constraint durumu** (`NOCHECK`/pasif) korunur; constraint yapısal **imza** ile
  karşılaştırılır — özdeş bir PK gereksiz drop+recreate edilmez (FK referansı patlamaz).
- **Güvenlik kademesi:** additive değişiklikler (nullable kolon, tip genişletme) doğrudan
  script'e girer; veri kaybı riski taşıyanlar (kolon/tablo silme, tip daraltma, dolu tabloya
  DEFAULT'suz NOT NULL) güvenli modda script'e **girmez**, ayrı listede SQL'iyle raporlanır.

Bilinçli kapsam dışı (tespit edilir ama scriptlenmez): **kolon fiziksel sırası** değişimi
(SQL kolonu tablo yeniden kurmadan sıralayamaz — fark görünür ama uygulanmaz; `IgnoreColumnOrder`
ile tümden yok sayılabilir), **mevcut tablonun filegroup'a taşınması** (clustered rebuild
gerektirir — yalnız yeni tabloda yerleşim yazılır), **veri taşıma**, **DROP SCHEMA**.

---

## CLI kullanımı

Toplu/otomasyon için. Karşılaştırmanın birimi bir **iş**tir: isimlendirilmiş `(kaynak, hedef)`
çifti. Hiçbir veritabanı adı/katman kavramı kaynak kodda geçmez — hepsi yapılandırmadan gelir.

```powershell
# Yapılandırma üret ve düzenle
dotnet run --project src\SchemaDiff.Cli -c Release -- --init-config jobs.json

# İşleri listele / etkileşimli seç / belirli işleri koş
SchemaDiff.Cli --config jobs.json --list
SchemaDiff.Cli --config jobs.json --select
SchemaDiff.Cli --config jobs.json --jobs "1,4-6,Katman9" --risk --triggers

# Yapılandırma dosyasız tek çift
SchemaDiff.Cli -s "<kaynak-conn>" -t "<hedef-conn>"

# Script üretimi
SchemaDiff.Cli -s "<kaynak>" -t "<hedef>" --script deploy.sql                 # tümü
SchemaDiff.Cli -s "<kaynak>" -t "<hedef>" --script deploy.sql --safe          # veri kaybı adımlarını ÇIKAR
SchemaDiff.Cli -s "<kaynak>" -t "<hedef>" --script deploy.sql --rollback rollback.sql   # ileri + geri
SchemaDiff.Cli -s "<kaynak>" -t "<hedef>" --script deploy.sql --script-scope modules|tables
```

Seçilen işler paralel koşar ve **bittikçe** ekrana düşer (`Task.WhenEach`); bir işin
başarısız olması diğerlerini durdurmaz. Çıkış kodları: `0` fark yok, `1` fark var, `2` hata.
`--max-queries` (varsayılan 16) eşzamanlı sorgu sayısını sınırlar.

Kapsamı **ölçmek** için: `SchemaDiff.Cli -s "<conn>" --coverage` — her obje sınıfının
veritabanında kaç tane olduğunu sayar ve kapsanmayan ama sayısı sıfırdan büyük olanları listeler.

---

## Risk analizi ve trigger raporu

**`--risk`** her tablo değişikliğini sınıflandırır ve hedefteki yaklaşık satır sayısıyla
raporlar — "deployment duracak mı?" sorusu *önceden* cevaplanır. Satır sayısı
`sys.dm_db_partition_stats`'tan okunur (tabloyu taramaz). Kural: **emin olunmayan her durum
daha riskli tarafa yuvarlanır.** Dolu tablo ile boş tablo ayrılır — boşta bloke yok.

**`--triggers`** 7 katmandaki trigger'ların aktif/pasif durumunu tek tabloda verir ve iki
ortam arasında **durumu farklı** olanları işaretler (web'de ayrıca "Triggers" sekmesi).

**Yetki:** bağlanan kullanıcıda **`VIEW DEFINITION`** olmalı; yoksa `sys.sql_modules.definition`
sessizce `NULL` döner ve modüller "aynı" görünür. Araç bunu tespit edip uyarır, ilgili objeleri
`Belirsiz` sayar — asla "aynı" demez. Risk satır sayıları için `VIEW DATABASE STATE` gerekir;
yoksa `?` gösterilir.

---

## Kapsam

**Karşılaştırılan ve script'e yazılan:** şemalar; tablolar (kolonlar, computed/identity,
default'lar, sparse/FILESTREAM/ROWGUIDCOL/COLUMN_SET, tipli XML); index'ler
(key/include/filter/columnstore + fiziksel seçenekler + **depolama yerleşimi**); XML ve
spatial index'ler; kullanıcı istatistikleri; PK-UQ-CHECK-FK constraint'leri ve
etkin/güvenilir durumları; temporal tablolar; view'lar, prosedürler, fonksiyonlar, DML ve
**DDL** trigger'ları; sequence'lar, synonym'ler; alias tipler; table type'lar; partition
function/scheme'ler; XML schema collection'lar; full-text katalog/index/stoplist; legacy
`RULE`/`DEFAULT`; roller ve üyelikleri; obje/kolon/şema/veritabanı izinleri; extended
property'ler; plan guide'lar; database scoped configuration.

**Yalnızca TESPİT edilir, script'i üretilmez** (geri dönüşü pahalı olduğundan insana bırakılır):
CLR assembly/tipleri, Always Encrypted / kolon şifreleme anahtarları, in-memory OLTP.

**Kapsam dışı:** veritabanı kullanıcıları (ortama özgü, varsayılan yok sayılır); mevcut
tablonun filegroup'a taşınması; kolon fiziksel sırası; veri taşıma.

---

## Testler

`main` branch'inde (`test/SchemaDiff.Tests`, xUnit). Çoğu DB gerektirmez; birkaç entegrasyon
testi yerel SQL Server + fixture ister ve o yoksa atlanır.

```powershell
dotnet test test\SchemaDiff.Tests\SchemaDiff.Tests.csproj -c Release
```

**1.100+ test** saf mantığı izole doğrular: normalizasyon (string içi `--` yanılgısı dahil),
CREATE→ALTER token dönüşümü, karşılaştırma yönü, risk sınıflandırması, her obje sınıfının
kanonik metni, **her karşılaştırma seçeneğinin davranışı** (OFF→fark / ON→yok sayılır), ve
script üretiminin sıralama/placement/güvenlik kuralları. Testlerin çoğu bir davranışı değil,
geçmişte yaşanmış bir **hatayı kilitler** (ör. `ScriptCoverageMatrixTests` her `ObjectKind`
için ya SQL üretildiğini ya da adıyla/sebebiyle raporlandığını doğrular).

---

## Branch'ler ve .NET sürümleri

Üç branch aynı ürünü taşır, yalnız hedef çalışma zamanı ve küçük dil farkları değişir:

| Branch | Hedef | Açıklama |
|---|---|---|
| `main` | **.NET 10** | Ana geliştirme; birim testleri, `SchemaDiff.Bench`, `tools/`, `test/fixtures/` burada. |
| `main_clean` | **.NET 10** | `main`'in sade kopyası. |
| `main_clean_8` | **.NET 8** | .NET 8 geri-portu — net8 çalışma zamanı olmayan makineler için dağıtım paketi. |

Kaynak kod farkı yalnızca üç dosyada ve hepsi dil/kütüphane sürümünden kaynaklanır:

| Dosya | net10 | net8 |
|---|---|---|
| `Web/CompareService.cs` | `System.Threading.Lock` | `object` + `lock` |
| `Core/Jobs/ComparisonRunner.cs` | `Task.WhenEach` | `Task.WhenAny` döngüsü |
| `Core/SchemaDiff.Core.csproj` | `System.IO.Hashing` 10.x | 8.x |

Davranış aynıdır. `.slnx` solution formatı SDK **9.0.200+** ister.

---

## Yapı

```
src/SchemaDiff.Core/
  Extraction/Sql.cs              katalog sorguları (salt-okunur SELECT'ler)
  Extraction/CatalogExtractor.cs paralel çekim + yetki kontrolü
  Extraction/SnapshotBuilder.cs  kanonikleştirme + hash + seçenek uygulaması
  Normalization/                 ScriptDom tabanlı T-SQL normalizasyonu
  Hashing/Hash.cs                XxHash128 + parça birleştirme
  Diff/SchemaComparer.cs         hash tabanlı karşılaştırma
  Analysis/                      risk sınıflandırması, değişiklik ağacı, rename sezgisi
  Scripting/                     script üreteçleri (tip → tablo → modül → rol → EP/izin → ayar)
  Jobs/                          iş modeli, paralel akan koşum, yapılandırma, seçim
src/SchemaDiff.Cli/              seçim arayüzü ve raporlar
src/SchemaDiff.Web/              web arayüzü (minimal API + SSE + bağımlılıksız SPA)
                                 ScriptExecutor.cs — script'i DB'de çalıştırma (SSMS tarzı)
```

Teknolojiler: **.NET 8/10, C#**, `Microsoft.Data.SqlClient`,
`Microsoft.SqlServer.TransactSql.ScriptDom` (SSDT'nin kullandığı T-SQL parser),
`System.IO.Hashing` (XxHash128), ASP.NET Core Minimal API + SSE, bağımlılıksız SPA
(framework yok, tek `app.js`). `Jobs/` katmanı alan-bağımsızdır; CLI ve web aynı
`ComparisonRunner`'ı kullanır.

---

## Güvenlik notu

> ⚠️ **Yalnızca yerel makine.** Web sunucusu bilinçli olarak `127.0.0.1`'e bağlanır ve
> kimlik doğrulama **yoktur**. Bağlantı dizeleri sunucu tarafında durur, tarayıcıya hiç
> gönderilmez (arayüzde yalnız `sunucu.veritabanı` etiketi görünür). Ağa açmadan önce kimlik
> doğrulama eklenmelidir — aksi hâlde ağdaki herkes bu bağlantılarla sorgu koşturabilir.
> Araç, kendisinin dinlediği portu uzaktan erişime açmaz; karşılaştırılan SQL Server uzak
> olabilir ama arayüze yalnızca aynı makineden bağlanırsınız.
