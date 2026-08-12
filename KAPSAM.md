# KAPSAM — SchemaDiff neyi görür, neyi yazar, neyi görmez

Son güncelleme: 2026-08-12 (Dalga 13 sonrası)

Bu belge tek soruyu cevaplar: **"Bu araca güvenip deploy edersem neyi kaçırırım?"**

Üç ayrı yetenek var ve karıştırılmamalı:

| Sütun | Anlamı |
|---|---|
| **Görür** | Fark karşılaştırmaya girer; arayüzde ve raporda görünür. |
| **Yazar** | Dağıtım script'ine T-SQL olarak üretilir. |
| **Sayar** | `--coverage` sondası veritabanında kaç tane olduğunu ölçer. |

Kural: **görmediğimiz hiçbir şey sessiz kalmaz** — sayılır ve sayısı > 0 ise uyarılır.
En tehlikeli sonuç "fark yok" demektir; bu yüzden okunamayan her sınıf ayrı raporlanır.

---

## 1. Tam kapsam — görür ve yazar

| Sınıf | Ayrıntı |
|---|---|
| Şemalar | `CREATE SCHEMA` |
| Tablolar | Yeni tablo `CREATE`; kolon `ADD` / `ALTER COLUMN` / `DROP COLUMN` |
| Kolonlar | tip, uzunluk/precision/scale, NULL'lık, collation, computed (+PERSISTED), DEFAULT |
| IDENTITY | seed/increment + `NOT FOR REPLICATION` (ADD COLUMN dahil) |
| Kolon depolama nitelikleri | `SPARSE`, `FILESTREAM`, `ROWGUIDCOL`, `COLUMN_SET` |
| Tipli XML kolonları | `xml(CONTENT\|DOCUMENT [şema].[koleksiyon])` — koleksiyon ADIYLA kıyaslanır |
| Index'ler | key/`INCLUDE`/filtered/columnstore, `DATA_COMPRESSION`, `ALLOW_ROW_LOCKS`, `ALLOW_PAGE_LOCKS`, fill factor, `STATISTICS_NORECOMPUTE`, `OPTIMIZE_FOR_SEQUENTIAL_KEY` |
| PK / UNIQUE | `ALTER TABLE ADD CONSTRAINT`, clustered/nonclustered, compression |
| CHECK | tanım + `NOT FOR REPLICATION` + **durum** (pasif / güvenilmez) |
| FOREIGN KEY | çok kolonlu, `ON DELETE/UPDATE`, `NOT FOR REPLICATION` + **durum** |
| Kullanıcı istatistikleri | `CREATE STATISTICS` — kolon sırası, `WHERE`, `NORECOMPUTE`, `INCREMENTAL` |
| View / prosedür / fonksiyon | `CREATE` / `ALTER` / `DROP`, `ANSI_NULLS` + `QUOTED_IDENTIFIER` korunur |
| DML trigger | gövde + etkin/pasif durumu |
| DDL trigger | `ON DATABASE` |
| Sequence | `CREATE SEQUENCE` (current_value hariç — şema farkı değil) |
| Synonym | `CREATE SYNONYM` |
| Alias tipler | `CREATE TYPE … FROM` |
| Table type | kolon yapısı + DEFAULT + PK/UNIQUE/CHECK/index (tip ALTER edilemez, bkz. §2) |
| Partition function / scheme | `CREATE PARTITION FUNCTION/SCHEME` |
| Roller ve üyelikler | `CREATE ROLE`, `ALTER ROLE ADD MEMBER` (sabit rollerin ÜYELİĞİ dahil) |
| İzinler | obje / kolon / şema / **veritabanı** seviyesi `GRANT` / `DENY` |
| Extended property'ler | obje / kolon / şema / **veritabanı** seviyesi |
| Veritabanı kullanıcıları | `CREATE USER` (login/SID eşlemesi ortama özgü, kıyasa girmez) |
| Temporal (system-versioned) | `CREATE TABLE` tam üretir; **kapatma** tam ve güvenli |
| XML schema collection | `CREATE XML SCHEMA COLLECTION` (XSD okunabilirse); namespace'ler her hâlde kıyaslanır |
| Full-text katalog | `CREATE FULLTEXT CATALOG` — accent sensitivity, `AS DEFAULT` |
| XML index | `CREATE PRIMARY XML INDEX` / secondary `USING XML INDEX … FOR PATH\|VALUE\|PROPERTY` |
| Spatial index | `CREATE SPATIAL INDEX … USING …` — `BOUNDING_BOX`, `GRIDS`, `CELLS_PER_OBJECT` |
| Full-text index | `CREATE FULLTEXT INDEX` — KEY INDEX, katalog, `TYPE COLUMN`, `LANGUAGE`, `CHANGE_TRACKING`, `STOPLIST`, pasiflik |

---

## 2. Kısmi — görür ama yazmaz (ya da eksik yazar)

Bunlar rapora düşer, script'e girmez. Girmedikleri her seferinde **sebebiyle birlikte**
"atlandı" listesinde adları geçer — sessizce düşmezler.

| Konu | Neden yazılmıyor |
|---|---|
| Kolon **sırası** değişimi | SQL Server ALTER ile sırayı değiştiremez; tablo yeniden oluşturma + veri taşıma gerekir |
| IDENTITY / computed kolona dönüşüm | `ALTER COLUMN` ile yapılamaz |
| `FILESTREAM` / `COLUMN_SET` açma-kapama | ALTER ile yapılamaz, tablo yeniden oluşturulmalı |
| IDENTITY `NOT FOR REPLICATION` değişimi | ALTER ile yapılamaz |
| Temporal **açma** / history değişimi | PERIOD kolonları + DEFAULT gerektirir; yarım SQL yerine uyarı |
| View üzerindeki istatistikler | fark görünür; script view'ın kendi drop+create'inden gider |
| Rol sahibi (owner) değişimi | üretilmiyor |
| XML schema collection **değişimi** | `ALTER … ADD` yalnız EKLEYEBİLİR, çıkarma yoktur; fark görünür, uygulama elle |
| Table type **değişimi** | tip ALTER edilemez; fark GÖRÜNÜR, script için bağımlılıkları düşürüp elle drop+recreate gerekir |

---

## 3. Bilinçli tespit-only — banka için oto-script RİSKLİ

Sayılır ve uyarılır; script **kasıtlı olarak** üretilmez. Bu bir eksik değil, karardır:
yanlış üretilmiş bir güvenlik ya da kripto DDL'i, hiç üretilmemesinden kötüdür.

- Row-Level Security (security policy)
- Dynamic Data Masking (`MASKED WITH`)
- Always Encrypted (`ENCRYPTED WITH`, CMK/CEK)
- Sertifikalar, simetrik/asimetrik anahtarlar, database scoped credential'lar
- Service Broker (queue / service / contract)
- PolyBase external data source & table
- CLR: assembly, CLR tipleri, CLR prosedür/fonksiyonları
- In-memory OLTP tabloları
- Graph node/edge tabloları
- Application role'ler

---

## 4. Henüz kapsanmıyor — yapılacaklar

Sayılıyor (sayısı > 0 çıkarsa `--coverage` uyarır) ama görülmüyor ve yazılmıyor.
**Öncelik sırasıyla:**

| # | Konu | Neden bu sırada |
|---|---|---|
| 1 | Full-text stoplist objesi | Index artık stoplist'e ADIYLA başvuruyor; stoplist'in kendisi kapsam dışı |
| 2 | Table type drop+recreate üretimi | Fark artık görünüyor; bağımlılık (prosedür parametreleri) düşürme sırası gerekir |
| 3 | Plan guide · DB scoped configuration · legacy `RULE`/`DEFAULT` | Düz `CREATE`/`ALTER`, düşük risk, düşük sıklık |
| 4 | Parametre / principal seviyesi extended property | Obje/kolon/şema/db kapsandı, kalan uçlar |

**Bilinçli ertelenen:** fiziksel yerleşim — `ON [filegroup]`, `TEXTIMAGE_ON`,
`FILESTREAM_ON`, tablo/index'in partition scheme üzerine yerleşimi. Filegroup'ları biz
oluşturmuyoruz; `ON [DATA_FG]` yazarsak hedefte o filegroup yoksa `CREATE` patlar.
Bu yüzden yazmamak, yanlış yazmaktan güvenli.

---

## 5. Kapsam dışı — şema farkı DEĞİL

Bunlar kasıtlı olarak hiç kıyaslanmaz; kıyaslanırsa her karşılaştırma gürültüyle dolar.

| Konu | Neden |
|---|---|
| Otomatik üretilen istatistikler (`auto_created`) | Optimizer artefaktı, veriye göre oluşur |
| Index'in taşıdığı istatistikler | Index'in parçası, ayrı obje değil |
| Sequence `current_value` | Her kullanımda değişir |
| İstatistik örnekleme oranı | Katalogda tutulmaz, verinin o anki hâlini anlatır |
| Login / SID eşlemesi | Ortama özgü |
| Sistem üretimi constraint adları (`PK__Tbl__A1B2`) | Ortamlar arasında farklı (varsayılan; kapatılabilir) |
| Temporal history tablosunun otomatik adı | `MSSQL_TemporalHistoryFor_<id>` — id ortama özgü |

---

## 6. Okunamayan ≠ yok

Bir katalog sorgusu yetki ya da sürüm nedeniyle çalışmazsa, o sınıf **boş sayılmaz**:
karşılaştırma dışı bırakılır ve rapora uyarı düşer.

Sebebi somut: istatistik sorgusu eski sunucuda düşerse (`sys.stats.is_incremental`
SQL Server 2014 ile geldi) ve biz "istatistik yok" dersek, hedefte gerçekten var olan
istatistikler için `DROP STATISTICS` üretiriz. Aynı mantık `VIEW DEFINITION` yetkisi
olmayan modüller için de geçerlidir: sessizce "aynı" demek yerine **Belirsiz** raporlanır.

---

## 7. Kapsamı kendin ölç

```bash
dotnet run -c Release --project src/SchemaDiff.Cli -- --source "<conn>" --coverage
```

Sonda her sınıfın o veritabanında kaç tane olduğunu sayar. Kapsanmayan ve sayısı > 0
çıkanlar, o veritabanı için gerçek yapılacaklar listesidir — §4'teki genel sıralamadan
daha değerlidir, çünkü ölçüme dayanır.
