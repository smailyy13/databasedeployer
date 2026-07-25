# SchemaDiff

SQL Server şema karşılaştırması için hızlı bir motor. SSDT / VS Code "Schema Compare"
ile aynı soruyu cevaplar — *iki veritabanı arasında ne değişti?* — ama büyük
veritabanlarında dakikalar yerine saniyeler sürmesi hedeflenir.

> Durum: **Faz 0 (PoC)**. Karşılaştırma ve raporlama çalışır.
> Deployment script üretimi henüz **yok** — bkz. [Kapsam](#kapsam).

## Neden daha hızlı?

SSDT'nin altındaki DacFx, karşılaştırmadan önce her iki taraf için de tam bir
**semantik model** kurar: her objenin DDL'ini parse eder, isim çözümlemesi yapar,
bağımlılık grafiğini çıkarır, doğrulama çalıştırır. Bu obje başına pahalıdır ve
"ne değişti?" sorusu için gereksizdir.

Bu araç bunun yerine:

1. **Toplu katalog çekimi** — obje başına sorgu yok, obje *sınıfı* başına tek sorgu.
   17 sorgu paralel bağlantılarda koşar (`Sql.cs`).
2. **Kanonikleştirme** — modül gövdeleri ScriptDom'un *token akışıyla* normalize edilir
   (regex ile değil; regex string literal içindeki `--` ile yanılır).
3. **Hash karşılaştırması** — her obje ve alt-parçası için XxHash128. Eşit hash'li
   objenin metnine hiç bakılmaz. Dev↔prod arasında objelerin %95+'i eşit olduğundan
   pahalı iş yalnızca kalan azınlığa uygulanır.

Semantik model yalnızca *script üretimi* için gerekir (bağımlılık sıralaması), ki o da
`sys.sql_expression_dependencies` üzerinden çok daha ucuza elde edilebilir.

## Ölçüm

10.000 objeli iki veritabanı (3.000 tablo / 37.500 kolon / 9.000 index / 5.000 prosedür /
1.500 view / 500 fonksiyon), aralarında 255 gerçek fark var.
Aynı makine, yerel SQL Server, 3 koşu ortalaması:

| | Süre | Bulunan fark |
|---|---|---|
| **SchemaDiff** | **1.346 ms** | 255 |
| DacFx (`SchemaComparison` — SSDT'nin kullandığı API) | 32.862 ms | 255 |
| | **24,4x hızlı** | *aynı sonuç* |

Fark sayılarının birebir tutması önemli bir doğruluk sinyali. Yine de bu fixture
izinleri ve extended property'leri içermiyor; kapsam farkı bu ölçümde ortaya çıkmadı.

Süre dağılımı (10.000 obje, tek taraf):

| Aşama | Süre |
|---|---|
| Katalog çekimi (17 sorgu, paralel) | ~1.050 ms |
| Normalizasyon + hash | ~680 ms |
| Karşılaştırma | ~13 ms |

En pahalı tek sorgu `columns` (37.500 satır, ~800 ms) — ilk optimizasyon hedefi orası.
`Karşılaştırma` süresinin 13 ms olması, hash yaklaşımının işe yaradığının doğrudan
kanıtı: 9.770 eşit objenin metnine hiç bakılmıyor.

> Ölçüm koşulları: yerel instance, ağ gecikmesi yok; sentetik prosedür gövdeleri
> gerçek hayattakinden kısa. Uzak sunucu ve büyük gövdelerle mutlak süreler artar —
> ama artan kısım her iki araç için de aynı, oran korunur.

Benchmark'ı kendiniz koşmak için:

```powershell
dotnet run --project src\SchemaDiff.Bench -c Release -- "<source-conn>" "<target-conn>" 3
```

## Web arayüzü

```powershell
cd src\SchemaDiff.Web
dotnet run -c Release
```

Sonra tarayıcıdan **http://127.0.0.1:5290** (port doluysa `-- --port 5300`).

Düzen bilinçli olarak SSDT Schema Compare'e benzetildi:

- Üstte **kaynak (dev) ⇄ hedef (prod)** bağlantı seçicileri
- Bağlantı diyaloğu SSDT'nin *Connect* penceresiyle aynı alanları sorar: sunucu adı,
  kimlik doğrulama, kullanıcı/parola, veritabanı (sunucudan listelenir), Encrypt,
  Trust Server Certificate — ham bağlantı dizesi yazmanız gerekmez
- **Son bağlantılar** listesi (SSDT'deki *Recent Connections*) yeniden seçim için saklanır
- Sonuçlar **Delete / Change / Add** olarak gruplanır, tür sütunuyla (Table, View,
  Procedure, Trigger…); değişen objeler açılıp **Add Column / Alter Column / Delete Index**
  gibi alt değişikliklere inilir
- Altta **Object Definitions** paneli: seçili objenin **tam metni** iki taraflı, gerçek
  satır numaralarıyla. Prosedür/view/fonksiyonda özgün T-SQL gövdesi; tablolarda
  katalogdan üretilmiş `CREATE TABLE` script'i — SQL Server tablonun DDL metnini
  saklamaz, SSMS'in "Script Table as CREATE"i de aynısını üretir. Böylece değişiklik
  öncesi ve sonrasıyla birlikte görünür.
- ▲ ▼ tuşları fark blokları arasında gezinir (`3 / 12 fark`), blok görünümün ortasına gelir
- "kanonik metin" kutusu karşılaştırmanın gerçekte neyi hash'lediğini gösterir
- Ayırıcı sürüklenerek üst/alt panel oranı değiştirilebilir

Üretilen tablo script'i, karşılaştırmada yok sayılan şeyleri (sistem üretimi constraint
adları, collation) **yazmaz da**. Aksi hâlde ekranda fark görünüp listede görünmeyen
satırlar olur ve araca güven kalmazdı.
- **Deployment riski** ve **Trigger'lar** ayrı görünümler olarak durur

### Parola saklama

"Parolayı hatırla" işaretlenirse parola **Windows DPAPI** ile, oturum açan kullanıcı
hesabına bağlı olarak şifrelenip `%APPDATA%\SchemaDiff\recent.json` içine yazılır.
Başka bir hesap dosyayı okusa bile çözemez. Parola tarayıcıya **hiçbir yanıtta**
geri gönderilmez; kayıtlı bir bağlantı seçildiğinde sunucu tarafında çözülür.
Windows dışında parola hiç saklanmaz — düz metin yazmaktansa saklamamak doğru davranış.

> ⚠️ **Yalnızca yerel makine.** Sunucu bilinçli olarak `127.0.0.1`'e bağlanır ve kimlik doğrulama
> **yoktur**. Bağlantı dizeleri sunucu tarafında durur ve tarayıcıya hiç gönderilmez (arayüzde
> yalnızca `sunucu.veritabanı` etiketi görünür). Ağa açmadan önce kimlik doğrulama eklenmelidir —
> aksi hâlde ağdaki herkes bu bağlantılarla sorgu koşturabilir.

## Dağıtım script'i üretimi

```powershell
# Modüller + tablolar (varsayılan), veri kaybı adımları hariç
SchemaDiff.Cli -s "<kaynak>" -t "<hedef>" --script deploy.sql

# Yalnızca modüller ya da yalnızca tablolar
SchemaDiff.Cli -s "<kaynak>" -t "<hedef>" --script deploy.sql --script-scope modules
SchemaDiff.Cli -s "<kaynak>" -t "<hedef>" --script deploy.sql --script-scope tables

# Güvenli mod: veri kaybı adımlarını (kolon/tablo silme, tip daraltma) script'ten ÇIKAR
SchemaDiff.Cli -s "<kaynak>" -t "<hedef>" --script deploy.sql --safe
```

> **Not (şimdilik):** SSDT paritesi için veri kaybı adımları **varsayılan olarak script'e
> dahildir**. Güvenli mod (ayrı listede raporla, script'e koyma) için `--safe` kullanın.
> Web'de "Script üret" de aynı şekilde veri kaybı adımlarını dahil eder ve durum çubuğunda uyarır.

Web arayüzünde: karşılaştırmadan sonra **Script üret** düğmesi. Sonuç ağacındaki işaret
kutularıyla **yalnızca seçtiğiniz objeleri** yazdırabilirsiniz — bir alt öğeyi (kolon/izin)
işaretlemek o objeyi de script'e alır; hiçbir şey seçmezseniz tümü yazılır. Üretilen script'i
elle çalıştırdığınızda seçtiğiniz değişiklikler iki veritabanı arasında eşitlenir (üretilen
kapsam dahilinde — bkz. aşağıdaki kapsam sınırı).

**Modüller** (view, prosedür, fonksiyon, trigger + şemalar) — veri kaybı imkânsız, en güvenli dilim:

- `CREATE` / `ALTER` / `DROP`, `OBJECT_ID` korumasıyla
- `CREATE`→`ALTER` dönüşümü ScriptDom **token** düzeyinde yapılır; metin araması olsaydı
  yorumdaki ya da string içindeki "create" kelimesi bozulurdu
- Yeni objeler `sys.sql_expression_dependencies` ile topolojik sırada oluşturulur
- Her modülün özgün `ANSI_NULLS` / `QUOTED_IDENTIFIER` ayarı korunur

**Tipler / sequence / synonym** — kullanıcı tanımlı alias tipler, table type'lar,
sequence'lar (`CREATE SEQUENCE`) ve synonym'ler (`CREATE SYNONYM`). Tablo/modül
dilimlerinden ÖNCE çalışır (sıra: sequence → alias tip → table type → synonym; tablo
default'ları sequence kullanabilir). Bunlar ALTER edilmez; DEĞİŞEN obje atlanır, `DROP` opt-in.

**Roller** — kullanıcı tanımlı roller ve üyelikleri (`CREATE ROLE` / `DROP ROLE` /
`ALTER ROLE ADD|DROP MEMBER`). Veri kaybı yok; hedefte olmayan üye (principal) atlanır.

**İzinler** — `GRANT` / `DENY` / `REVOKE` (obje/kolon/şema). Her ifade grantee'nin
varlığını kontrol eder; eksik principal atlanır. GRANT→DENY dönüşümü REVOKE + DENY olur.

**Extended property'ler** — `sp_addextendedproperty` / `sp_updateextendedproperty` /
`sp_dropextendedproperty` (obje/kolon/şema). İzin ve EP dilimleri modül+tablo+rol
dilimlerinden sonra çalışır (host obje ve grantee var olmalı); yalnızca birleşik (`all`) kapsamda.

**Tablolar** — yeni tablo `CREATE`, kolon `ADD` / `ALTER COLUMN` / `DROP COLUMN`, ve
index / PK / UNIQUE / CHECK / FK değişiklikleri (aynı ad + farklı tanım = drop + recreate;
yapısal İMZA ile karşılaştırılır, özdeş constraint gereksiz drop edilmez):

- **Güvenlik kademesi:** additive değişiklikler (nullable kolon, tip genişletme) doğrudan
  script'e girer. Veri kaybı riski taşıyanlar (kolon/tablo silme, tip daraltma, dolu tabloya
  DEFAULT'suz NOT NULL kolon) `--allow-data-loss` verilmedikçe script'e **GİRMEZ**; ayrı
  listede ismen raporlanır. Boş tablo ile dolu tablo ayrılır — boşta bloke yok.
- IDENTITY/computed kolon değişimi ALTER COLUMN ile yapılamaz → atlanır, sebebiyle bildirilir
- Tablolar modüllerden önce yayınlanır (yeni view/prosedürler yeni tablolara bağlı olabilir)

Tümü tek transaction + `SET XACT_ABORT ON`.

> ⚠️ **Kalan kapsam dışı:** kolon SIRASI değişimi (ortaya kolon ekleme → tablo yeniden
> oluşturma), partition DDL, veri taşıma. Bunlar da başlıkta/arayüzde açıkça raporlanır —
> "script'te yoktu" ile "değişiklik yoktu" karıştırılmasın.

## Kavram: İş (job)

Karşılaştırmanın birimi bir **iş**tir: isimlendirilmiş bir `(kaynak, hedef)` çifti.

```csharp
record ComparisonJob(string Name, string SourceConnectionString, string TargetConnectionString);
```

Bilinçli olarak **hiçbir varsayım yok**: iki taraf farklı sunucuda olabilir, farklı
veritabanı adı taşıyabilir, sayıları kaç olursa olsun. Hangi çiftlerin karşılaştırılacağı
bir yapılandırma sorusudur, kod sorusu değil — bu yüzden hiçbir veritabanı adı,
katman listesi ya da kuruma özgü kavram kaynak kodunda geçmez.

## Kullanım

### 1. Yapılandırma dosyası üret

```powershell
dotnet run --project src\SchemaDiff.Cli -c Release -- --init-config jobs.json
```

`jobs.json` düzenlenebilir bir iş listesidir:

```jsonc
{
  // Her işte tekrar yazmamak için ortak bağlantılar
  "defaults": {
    "source": "Server=KAYNAK;Integrated Security=true;TrustServerCertificate=true",
    "target": "Server=HEDEF;Integrated Security=true;TrustServerCertificate=true"
  },
  "jobs": [
    { "database": "Katman1" },                                  // iki tarafta aynı ad
    { "database": "Katman2" },
    { "name": "capraz", "sourceDatabase": "A", "targetDatabase": "B" },   // farklı adlar
    { "name": "ayri",                                            // defaults'u tamamen ezer
      "source": "Server=X;Database=..;Integrated Security=true;TrustServerCertificate=true",
      "target": "Server=Y;Database=..;Integrated Security=true;TrustServerCertificate=true" }
  ]
}
```

### 2. İşleri gör ve seç

```powershell
SchemaDiff.Cli --config jobs.json --list      # tanımlı işleri listele
SchemaDiff.Cli --config jobs.json --select    # etkileşimli seç
```

```
Karşılaştırılacak işleri seçin:

    1) Katman1     KAYNAK.Katman1  →  HEDEF.Katman1
    2) Katman2     KAYNAK.Katman2  →  HEDEF.Katman2
    ...

Seçim  (1,3  |  2-5  |  ad  |  a = hepsi  |  boş = iptal):
```

Aynı ifade komut satırından da verilebilir — elle yapılan bir seçim birebir aynı
şekilde script'e taşınır:

```powershell
SchemaDiff.Cli --config jobs.json --jobs "1,4-6,Katman9" --risk --triggers
SchemaDiff.Cli --config jobs.json --all -d 0
```

### 3. Sonuçlar bittikçe akar

Seçilen işler paralel koşar. `Task.WhenAll` yerine `Task.WhenEach` kullanılır: bir iş
30 saniye sürüyorsa diğerlerinin sonucunu bekletmenin anlamı yok, her iş **bittiği anda**
ekrana düşer.

```
9 iş paralel koşuyor (eşzamanlı sorgu sınırı: 16). Biten anında yazılır:

[1/9] ✓ Katman5              447 ms   obje 5 | aynı 1 | +0 -0 ~4
[2/9] ✓ Katman1              489 ms   obje 5 | aynı 1 | +0 -0 ~4
...
[9/9] ✓ buyuk              1.826 ms   obje 10.000 | aynı 9.770 | +25 -25 ~205
```

Ekrana düşme sırası **tamamlanma** sırasıdır; sondaki özet tablosu ise **seçim**
sırasındadır — katman sırası çoğu zaman anlamlıdır (deployment sırası gibi).

Bir işin başarısız olması diğerlerini durdurmaz; hata o satırda raporlanır ve
çıkış kodu `2` olur. Çıkış kodları: `0` fark yok, `1` fark var, `2` hata.

`--max-queries` eşzamanlı sorgu sayısını sınırlar (varsayılan 16). Sınırsız bırakılırsa
9 iş × 18 sorgu × 2 taraf = 324 eşzamanlı bağlantı açılır ve sunucu boğulur.

### Kısayol: yapılandırma dosyasız

Tek çift ya da iki sunucuda aynı adlı veritabanları için dosya gerekmez:

```powershell
SchemaDiff.Cli -s "<kaynak-conn>" -t "<hedef-conn>"                    # tek çift
SchemaDiff.Cli -s "<kaynak>" -t "<hedef>" --databases A,B,C --all      # aynı adlılar
```

## Deployment risk analizi (`--risk`)

Deployment prosedürünün en can sıkıcı maddesi şu: *"canlı ortamda veri olan bir tabloda
değişiklik yapmasına sistemin izin vermiyor... değişiklik yapılacak bir tabloda mutlaka
ama mutlaka veri olmamalıdır."* Bu bilgi bugün **deployment anında** öğreniliyor.

`--risk`, her tablo değişikliğini sınıflandırır ve hedefteki yaklaşık satır sayısıyla
birlikte raporlar — yani "duracak mı?" sorusu deployment'tan önce cevaplanır:

```
  [VERİ KAYBI]  EDWSTG.dbo.FactSale   (5.000 satır)
      Description   nvarchar(500) → nvarchar(100) (daraltma — sığmayan veri kırpılır)
      Amount        decimal(18,4) → decimal(18,2) (hassasiyet düşüyor)
      ObsoleteCode  Kolon siliniyor (varchar(10)) — içindeki veri kaybolur
      Category      NULL kabul eden kolon NOT NULL yapılıyor
      TenantId      NOT NULL kolon ekleniyor (int) ve DEFAULT tanımı yok
      Quantity      smallint → int (güvenli tip genişletmesi)

  [boş tablo]   EDWSTG.dbo.DimEmpty   (0 satır)
      ... aynı değişiklikler, ama tablo boş olduğu için sorunsuz uygulanır
```

Satır sayısı `sys.dm_db_partition_stats`'tan okunur — tabloyu **taramaz**, milisaniyeler
sürer. `VIEW DATABASE STATE` yetkisi yoksa karşılaştırma yine çalışır, yalnızca risk
raporunda satır sayıları `?` görünür ve o tablolar "elle kontrol edin" olarak işaretlenir.

Sınıflandırma kuralı: **emin olunmayan her durum daha riskli tarafa yuvarlanır.**
Yanlış "güvenli" demek, yanlış "riskli" demekten çok daha pahalıdır. Aynı nedenle,
değişmiş olup kolon seviyesinde risk bulunmayan tablolar da rapordan düşürülmez —
raporda görünmemek "sorun yok" anlamına gelmemeli.

## Trigger durum raporu (`--triggers`)

Deployment prosedürünün 20. adımı, 7 katmandaki trigger'ların aktif/pasif durumunu
elle sorgulamayı gerektiriyor. `--triggers` bunu tek tabloda verir ve ayrıca iki ortam
arasında **durumu farklı olan** trigger'ları işaretler:

```
  [!] 7 trigger'ın durumu iki ortamda farklı:
      EDWSTG.dbo.trg_FactSale_Guard: kaynak=aktif, hedef=PASİF
```

### Yetki gereksinimi

Bağlanan kullanıcıda **`VIEW DEFINITION`** olmalı. Yoksa `sys.sql_modules.definition`
sessizce `NULL` döner ve tüm modüller "aynı" görünür. Araç bunu tespit edip uyarır ve
ilgili objeleri `Belirsiz` olarak raporlar — asla "aynı" saymaz.

## Kapsam

Karşılaştırılan: şemalar, tablolar (kolonlar, computed/identity, default'lar),
index'ler (key/include/filter/columnstore), PK-UQ-CHECK-FK constraint'leri,
view'lar, prosedürler, fonksiyonlar, DML trigger'ları, sequence'lar, synonym'ler,
extended property'ler (obje/kolon/şema — `--ignore-extended-properties`), kullanıcı
tanımlı roller ve üyelikleri, obje/şema izinleri (GRANT/DENY — `--ignore-permissions`),
kullanıcı tanımlı alias tipler, table type'lar (kolon yapısı), partition function ve scheme'ler.

**Henüz kapsanmıyor** (sessizce atlanır — kullanmadan önce dikkate alın):
veritabanı kullanıcıları (ortama özgü), veritabanı seviyesi izinler, CLR tipleri,
table type constraint/index'leri, veritabanı seviyesi extended property'ler,
full-text, CLR assembly, temporal ve in-memory OLTP tabloları, Always Encrypted,
DDL trigger'ları.

## Doğruluk testleri

**Birim testleri** (`test/SchemaDiff.Tests`, xUnit — DB gerektirmez):

```powershell
dotnet test
```

78 test saf mantığı izole doğrular: normalizasyon (string içi `--` yanılgısı dahil),
CREATE→ALTER token dönüşümü, karşılaştırma yönü, risk sınıflandırması ve script
üretiminin güvenlik kademesi (additive vs veri kaybı).

**Fixture'lar** (`test/fixtures/`) — canlı DB'ye karşı uçtan uca. `expected.md` hem
bulunması gerekenleri hem de **bulunmaması gerekenleri** listeler — ikincisi en az
birincisi kadar önemli, çünkü gürültü üreten bir araca kimse güvenmez.

```powershell
sqlcmd -S localhost -E -C -b -i test\fixtures\dev.sql
sqlcmd -S localhost -E -C -b -i test\fixtures\prod.sql
```

Benchmark için sentetik büyük veritabanı:

```powershell
sqlcmd -S localhost -E -C -b -i test\fixtures\generate-large.sql `
       -v DbName="SchemaDiff_Big1" Tables=3000 Procs=5000 Views=1500 Funcs=500 Drift=0
sqlcmd -S localhost -E -C -b -i test\fixtures\generate-large.sql `
       -v DbName="SchemaDiff_Big2" Tables=3000 Procs=5000 Views=1500 Funcs=500 Drift=1
```

`Drift=1`, kontrollü bir fark kümesi üretir — böylece benchmark gerçekçi durumu ölçer:
birbirine %97 benzeyen iki veritabanı.

Risk analizi ve çok katmanlı mod için EDW benzeri kurulum (`layers.sql`). Kaynak
tarafı LocalDB, hedef tarafı varsayılan instance olacak şekilde iki ayrı sunucu kurar:

```powershell
foreach ($l in 'EDWSTG','EDWSKEY','EDWLRY','EDW','EDWDM','EDWArchive','EDWBridge') {
  sqlcmd -S "(localdb)\MSSQLLocalDB" -E -C -b -i test\fixtures\layers.sql -v DbName="$l" Variant="DEV"
  sqlcmd -S localhost               -E -C -b -i test\fixtures\layers.sql -v DbName="$l" Variant="PROD"
}
```

Bu fixture'daki kritik ayrım: `FactSale` hedefte **doludur**, `DimEmpty` ise aynı riskli
değişiklikleri içerir ama **boştur**. İkisini ayırt edebilmek risk analizinin tüm değeri.

## Temizlik

```powershell
sqlcmd -S localhost -E -C -Q "DROP DATABASE SchemaDiff_Dev; DROP DATABASE SchemaDiff_Prod; DROP DATABASE SchemaDiff_Big1; DROP DATABASE SchemaDiff_Big2;"

foreach ($l in 'EDWSTG','EDWSKEY','EDWLRY','EDW','EDWDM','EDWArchive','EDWBridge') {
  sqlcmd -S localhost               -E -C -Q "DROP DATABASE [$l];"
  sqlcmd -S "(localdb)\MSSQLLocalDB" -E -C -Q "DROP DATABASE [$l];"
}
```

## Yapı

```
src/SchemaDiff.Core/
  Extraction/Sql.cs              katalog sorguları
  Extraction/CatalogExtractor.cs paralel çekim + yetki kontrolü
  Extraction/SnapshotBuilder.cs  kanonikleştirme + hash
  Normalization/                 ScriptDom tabanlı T-SQL normalizasyonu
  Diff/SchemaComparer.cs         hash tabanlı karşılaştırma
  Analysis/                      deployment risk sınıflandırması
  Jobs/                          iş modeli, paralel akan koşum, yapılandırma, seçim
src/SchemaDiff.Cli/              seçim arayüzü ve raporlar
src/SchemaDiff.Web/              web arayüzü (minimal API + SSE + bağımlılıksız SPA)
src/SchemaDiff.Bench/            DacFx ile head-to-head karşılaştırma
```

`Jobs/` katmanı bilinçli olarak alan-bağımsızdır: `ComparisonRunner` yalnızca
`IReadOnlyList<ComparisonJob>` alır ve `IAsyncEnumerable<JobResult>` döner. Web
arayüzü geldiğinde CLI'nin yerine geçecek olan şey aynı iki tipi kullanacak;
paralellik, hata yalıtımı ve akış mantığı yeniden yazılmayacak.

## Sıradaki adımlar

1. **Gerçek ortamda ölçüm** — kendi dev/prod katmanlarında koşup hem gerçek süreyi
   hem de hangi obje sınıflarının eksik kaldığını somut olarak görmek. Faz 1'in
   kapsamı tahminle değil bu çıktıyla belirlenmeli.
2. **Kapsamı tamamla** — kullanıcı tanımlı tipler, izinler, extended property'ler.
   Bunlar bitmeden DacFx ile kıyas tam adil değil.
3. **Snapshot cache** — prod tarafı nadiren değişir; sıkıştırılmış snapshot'ı
   yeniden kullanmak ikinci karşılaştırmayı milisaniyelere indirir.
   Dikkat: `sys.objects.modify_date` her değişiklikte güncellenmez (örneğin
   `CREATE INDEX` tablonunkini değiştirmez), bu yüzden tek başına invalidation
   sinyali olarak kullanılamaz.
4. **Web arayüzü** — API + sanallaştırılmış ağaç + Monaco diff.
5. **Script üretimi** — en uzun ve en riskli faz; veri kaybı riski taşıyan
   değişiklikler ayrı onay istemeli.

### Kapsam dışı bırakılanlar

Deployment prosedürünün diğer adımları (SSIS paket deployment, `ParallelRun` /
`TableNullControl` tablolarının veri aktarımı, SSISDB parametre güncellemeleri,
SQL Job tetikleme) bu aracın kapsamında değil. Araç yalnızca prosedürün 3. adımını
— şema karşılaştırma ve aktarımı — hedefliyor.

İki adım ileride kolayca eklenebilir çünkü aynı veri zaten çekiliyor:
- Adım 6-7'deki `ParallelRun` karşılaştırması aslında bir **veri** karşılaştırması
  (`EXCEPT` sorguları). Kontrol/konfigürasyon tabloları için satır bazlı karşılaştırma
  eklenebilir.
- Adım 20'deki trigger durum kontrolü zaten `--triggers` ile yapılıyor.
