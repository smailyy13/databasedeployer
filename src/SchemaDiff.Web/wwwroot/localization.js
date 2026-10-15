'use strict';

// Display labels only: API identifiers, SQL, object names and query results stay unchanged.
const UI_LABELS_TR = {
  Table: 'Tablo', View: 'Görünüm', Procedure: 'Prosedür',
  'Scalar Function': 'Skaler Fonksiyon', 'Inline Function': 'Satır İçi Fonksiyon',
  'Table Function': 'Tablo Fonksiyonu', Trigger: 'Tetikleyici', 'DDL Trigger': 'DDL Tetikleyicisi',
  Schema: 'Şema', Synonym: 'Eş Anlamlı Nesne', Sequence: 'Sıra Nesnesi', Role: 'Rol', User: 'Kullanıcı',
  'User-Defined Type': 'Kullanıcı Tanımlı Tip', 'Table Type': 'Tablo Tipi',
  'Partition Function': 'Bölümleme Fonksiyonu', 'Partition Scheme': 'Bölümleme Şeması',
  'Full-Text Catalog': 'Tam Metin Kataloğu', 'Full-Text Stoplist': 'Tam Metin Durdurma Listesi',
  'XML Schema Collection': 'XML Şema Koleksiyonu', 'Plan Guide': 'Plan Kılavuzu',
  'Rule or Default': 'Kural veya Varsayılan', Database: 'Veritabanı', Unknown: 'Bilinmeyen',
  Add: 'Ekle', Change: 'Değiştir', Delete: 'Sil',
  Columns: 'Kolonlar', Column: 'Kolon', 'Primary Key': 'Birincil Anahtar',
  'Unique Constraints': 'Benzersizlik Kısıtları', 'Unique Constraint': 'Benzersizlik Kısıtı',
  Indexes: 'İndeksler', Index: 'İndeks', 'Foreign Keys': 'Yabancı Anahtarlar', 'Foreign Key': 'Yabancı Anahtar',
  'Check Constraints': 'Kontrol Kısıtları', 'Check Constraint': 'Kontrol Kısıtı',
  'Default Constraints': 'Varsayılan Kısıtları', 'Default Constraint': 'Varsayılan Kısıtı',
  Properties: 'Özellikler', Body: 'Gövde', 'Set Options': 'SET Seçenekleri', State: 'Durum', Owner: 'Sahip', Name: 'Ad',
  'Schema Content': 'Şema İçeriği', Storage: 'Depolama', Placement: 'Yerleşim',
  'System Versioning': 'Sistem Sürümleme', 'Extended Properties': 'Genişletilmiş Özellikler',
  'Extended Property': 'Genişletilmiş Özellik', Permissions: 'İzinler', Permission: 'İzin',
  Membership: 'Üyelikler', Member: 'Üye', Statistics: 'İstatistikler',
  'Database Settings': 'Veritabanı Ayarları', 'Scoped Configuration': 'Kapsamlı Yapılandırma',
  'Full-Text': 'Tam Metin', 'Full-Text Index': 'Tam Metin İndeksi',
  'XML Indexes': 'XML İndeksleri', 'XML Index': 'XML İndeksi',
  'Spatial Indexes': 'Uzamsal İndeksler', 'Spatial Index': 'Uzamsal İndeks',
  '(table)': '(tablo)', '(performance)': '(performans)', '(structure)': '(yapı)',
  '(schema)': '(şema)', '(column order)': '(kolon sırası)',
};

function uiLabel(text, language) {
  return language === 'tr' ? (UI_LABELS_TR[text] ?? text) : text;
}

function autocompleteLabel(kind, language) {
  const labels = language === 'tr'
    ? { kw: 'Anahtar kelime', type: 'Tip', fn: 'Fonksiyon', schema: 'Şema', table: 'Tablo', obj: 'Nesne', col: 'Kolon', id: 'Tanımlayıcı' }
    : { kw: 'Keyword', type: 'Type', fn: 'Function', schema: 'Schema', table: 'Table', obj: 'Object', col: 'Column', id: 'Identifier' };
  return labels[kind] ?? kind;
}

// Complete, anchored templates for application-authored risk descriptions. Captured SQL
// and identifiers are inserted verbatim, never translated with substring replacements.
const RISK_MESSAGES = [
  ['Yeni tablo oluşturuluyor ({n} kolon) — hedefte yok, risk yok', 'Creating a new table ({n} columns) — absent from target, no risk'],
  ['Tablo siliniyor', 'Dropping the table'],
  ["~{n} satır: ALTER işlemi tabloyu kilitler, uzun sürebilir ve log'u büyütür", '~{n} rows: ALTER locks the table, may take time and grow the log'],
  ['Değişen bölümler: {parts} — veri korunur, büyük tabloda süre alabilir', 'Changed parts: {parts} — data is preserved; large tables may take time'],
  ['Değişen bölümler: {parts} — kolon yapısında risk bulunmadı', 'Changed parts: {parts} — no column-structure risk found'],
  ["DROP SCHEMA [{schema}] — şema boş değil ({n} nesne). Boş olmayan şema silinemez; içindeki nesneler bu deploy'da düşürülmezse burada durur.", 'DROP SCHEMA [{schema}] — the schema is not empty ({n} objects). A nonempty schema cannot be dropped; deployment stops here unless its objects are dropped first.'],
  ['Kolon ekleniyor ({type})', 'Adding a column ({type})'],
  ['NOT NULL kolon ekleniyor ({type}) ve DEFAULT tanımı yok — dolu tabloda başarısız olur', 'Adding a NOT NULL column ({type}) without a DEFAULT — fails on a populated table'],
  ['Kolon siliniyor ({type}) — içindeki veri kaybolur', 'Dropping a column ({type}) — its data will be lost'],
  ['Kolon sırası değişiyor — araç ALTER üretir, sıra hedefteki gibi kalır (SSDT bunu tabloyu yeniden oluşturarak yapardı; bu araç veri taşıma üretmez)', 'Column order changes — ALTER preserves target order (SSDT would rebuild the table; this tool does not generate data migration)'],
  ['IDENTITY özelliği değişiyor — ALTER COLUMN ile yapılamaz, tablo yeniden oluşturulmalı', 'IDENTITY changes — ALTER COLUMN cannot apply this; the table must be rebuilt'],
  ['Normal kolon computed kolona dönüşüyor — mevcut değerler kaybolur', 'A regular column becomes computed — existing values will be lost'],
  ['Computed kolon normal kolona dönüşüyor — tablo yeniden oluşturulmalı', 'A computed column becomes regular — the table must be rebuilt'],
  ['NULL kabul eden kolon NOT NULL yapılıyor — mevcut satırlarda NULL varsa başarısız olur', 'A nullable column becomes NOT NULL — fails if existing rows contain NULL'],
  ["Collation değişiyor ({from} → {to}) — kolon index'liyse index önce düşürülmeli", 'Collation changes ({from} → {to}) — indexes on this column must be dropped first'],
  ['DEFAULT değişiyor ({from} → {to}) — yalnızca yeni satırları etkiler, mevcut veri değişmez', 'DEFAULT changes ({from} → {to}) — affects new rows only; existing data is unchanged'],
  ['{kind} ekleniyor ({keys}) — dolu tabloda tekrar eden değer varsa başarısız olur', 'Adding {kind} ({keys}) — fails if a populated table has duplicate values'],
  ['Index ekleniyor ({keys}) — veri korunur ama büyük tabloda süre alır ve tabloyu kilitler', 'Adding an index ({keys}) — preserves data but can take time and lock large tables'],
  ['{kind} kaldırılıyor ({keys}) — veri korunur', 'Removing {kind} ({keys}) — data is preserved'],
  ['CHECK ekleniyor ({expression}) — mevcut satırlar kuralı ihlal ediyorsa başarısız olur', 'Adding CHECK ({expression}) — fails if existing rows violate the constraint'],
  ['CHECK kaldırılıyor — veri korunur', 'Removing CHECK — data is preserved'],
  ['FOREIGN KEY ekleniyor ({columns} → {reference}) — eşleşmeyen değer taşıyan satır varsa başarısız olur', 'Adding FOREIGN KEY ({columns} → {reference}) — fails if rows contain unmatched values'],
  ['FOREIGN KEY kaldırılıyor — veri korunur', 'Removing FOREIGN KEY — data is preserved'],
  ['{from} → {to} (genişletme)', '{from} → {to} (widening)'],
  ["{from} → {to} (MAX'tan daraltma — veri kırpılır)", '{from} → {to} (narrowing from MAX — data will be truncated)'],
  ['{from} → {to} (daraltma — sığmayan veri kırpılır)', '{from} → {to} (narrowing — values that do not fit will be truncated)'],
  ['{from} → {to} (hassasiyet düşüyor — yuvarlama/taşma)', '{from} → {to} (reduced precision — rounding/overflow)'],
  ['{from} → {to} (güvenli tip genişletmesi)', '{from} → {to} (safe type widening)'],
  ['{from} → {to} (tip değişiyor — dönüşüm kayıplı olabilir)', '{from} → {to} (type changes — conversion may lose data)'],
];

function messagePattern(template) {
  const names = [];
  const escaped = template.split(/(\{\w+\})/).map((part) => {
    if (/^\{\w+\}$/.test(part)) { names.push(part.slice(1, -1)); return '([\\s\\S]*?)'; }
    return part.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }).join('');
  return { regex: new RegExp(`^${escaped}$`), names };
}
const RISK_TRANSLATIONS = RISK_MESSAGES.map(([tr, en]) => ({ ...messagePattern(tr), en }));
function riskText(text, language) {
  if (language !== 'en') return text;
  for (const { regex, names, en } of RISK_TRANSLATIONS) {
    const match = regex.exec(text);
    if (!match) continue;
    const values = Object.fromEntries(names.map((name, i) => [name, match[i + 1]]));
    return en.replace(/\{(\w+)\}/g, (_, name) => values[name]);
  }
  return text;
}

function riskColumnLabel(text, language) {
  if (language !== 'en') return text;
  const label = Object.entries(UI_LABELS_TR).find(([en, tr]) => en.startsWith('(') && tr === text);
  return label?.[0] ?? text;
}

function changeDetail(item, language) {
  const text = item.detail;
  if (!text || language !== 'en') return text;
  // Extended-property values, DEFAULT expressions and SQL definitions are user data.
  const exact = {
    'Gövde metni değişti': 'Body text changed', 'içerik değişti': 'Content changed',
    'SYSTEM_VERSIONING açılıyor': 'Enabling SYSTEM_VERSIONING',
    'SYSTEM_VERSIONING kapatılıyor': 'Disabling SYSTEM_VERSIONING',
    'history/PERIOD ayarı değişiyor': 'History/PERIOD settings change',
  };
  if (['Body', 'Schema Content', 'System Versioning'].includes(item.itemType)) return exact[text] ?? text;
  if (item.itemType === 'Column') {
    return text.split(', ').map((part) => {
      if (part === 'IDENTITY ekleniyor') return 'Adding IDENTITY';
      if (part === 'IDENTITY kaldırılıyor') return 'Removing IDENTITY';
      if (part === 'computed oluyor') return 'Becomes computed';
      if (part === 'computed olmaktan çıkıyor') return 'No longer computed';
      return part.replace(/^(SPARSE|FILESTREAM|ROWGUIDCOL|COLUMN_SET) (ekleniyor|kaldırılıyor)$/, (_, field, action) => `${action === 'ekleniyor' ? 'Adding' : 'Removing'} ${field}`);
    }).join(', ');
  }
  if (['Index', 'Primary Key', 'Unique Constraint', 'Foreign Key', 'Check Constraint', 'Statistics', 'Scoped Configuration', 'XML Index', 'Spatial Index', 'Full-Text Index'].includes(item.itemType))
    return text.replace(/^([\w, ]+) değişti$/, '$1 changed');
  return text;
}
