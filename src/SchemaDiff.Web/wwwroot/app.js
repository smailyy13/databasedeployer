'use strict';

const $ = (id) => document.getElementById(id);
let LANG = localStorage.getItem('lang') || 'en';
let THEME = localStorage.getItem('theme') || 'light';
const num = (n) => (n ?? 0).toLocaleString(LANG === 'tr' ? 'tr-TR' : 'en-US');
const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) =>
  ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

// ================= i18n (EN / TR) =================

const I18N = {
  en: {
    source: 'Source', target: 'Target', selectConnection: 'select connection',
    swap: 'Source ↔ Target', compare: 'Compare', generateScript: 'Generate Script',
    newComparison: 'New comparison', newComparisonTip: 'Open a new comparison in a separate tab',
    reverseTip: 'Mark for reverse (rollback) — included when you Generate Script',
    reverseIncluded: '{n} reversed in the reverse section',
    generateScriptTip: 'Generate a deployment script from selected changes',
    options: 'Comparison options', toggleTheme: 'Toggle theme', toggleLang: 'Language',
    pickTwo: 'Select two connections to compare.', allTypes: 'All types',
    searchPh: 'search schema or object…',
    tabDiff: 'Differences', tabRisk: 'Deployment risk', tabTriggers: 'Triggers',
    type: 'Type', objectDefinitions: 'Object Definitions',
    prevDiff: 'Previous difference', nextDiff: 'Next difference', canonicalText: 'canonical text',
    selectRow: 'Select a row above.', connect: 'Connect', recentConnections: 'Recent connections',
    none: 'None.', connectionProperties: 'Connection properties', serverName: 'Server name',
    authentication: 'Authentication', username: 'Username', password: 'Password',
    rememberPassword: 'Remember password', database: 'Database', listFirst: '— enter server —',
    authWindows: 'Windows Authentication', authSql: 'SQL Server Authentication',
    encrypt: 'Encrypt', encOptional: 'Optional (False)', encMandatory: 'Mandatory (True)',
    trustTrue: 'True', trustFalse: 'False',
    trustCert: 'Trust server certificate', testConnection: 'Test connection',
    cancel: 'Cancel', comparisonOptions: 'Comparison options', resetDefaults: 'Reset to defaults', ok: 'OK',
    connectSource: 'Source connection', connectTarget: 'Target connection',
    pwSaved: ' · password saved', listUnreadable: 'Could not read list.',
    storedPwHint: 'Stored password is resolved on the server — leave the field empty.',
    enterServerFirst: 'Enter the server name first.', listingDatabases: 'listing databases…',
    couldNotList: 'Could not list.', selectOpt: '— select —', databasesFound: '{n} databases found.',
    testing: 'testing…', couldNotConnect: 'Could not connect.',
    connectedAs: 'Connected — {server} ({version}), user {login}',
    serverRequired: 'Server name is required.', databaseRequired: 'Select a database.',
    startingCompare: 'starting comparison…', couldNotStart: 'Could not start.',
    comparing: 'comparing {source} → {target}…', connectionLost: 'Connection lost.',
    progressLine: '{source} → {target} · %{percent}{eta}',
    etaSuffix: ' · ~{sec} left', etaCalc: ' · estimating…',
    secShort: '{n}s', minShort: '{n}m',
    phaseConnecting: 'connecting', phaseExtracting: 'reading schema',
    phaseBuilding: 'building model', phaseComparing: 'comparing', phaseDone: 'finishing',
    notComparedYet: 'No comparison yet.', noDiffForFilters: 'No differences to show with these filters.',
    listLimited: 'List limited to {n} records — there are more.',
    flagIndeterminate: 'INDETERMINATE',
    pickInfo: '{n} objects selected · Shift+click for range',
    pickInfoAll: 'Nothing selected → the script will be empty',
    generatedScript: 'Generated script', downloadSql: 'Download .sql', copy: 'Copy',
    dirForward: 'source → target', dirReverse: 'target → source',
    emptyScriptInfo: 'No changes in this direction.',
    scriptReady: 'Script ready — review/edit, then download', scriptCopied: 'Script copied to clipboard',
    sqlDownloaded: 'Downloaded {file}', selectGroupTip: 'Select / clear all {g}',
    statResult: '{source} → {target} · {objects} objects · {equal} equal · +{add} ~{change} −{delete} · {ms} ms',
    renameWarn: ' · ⚠ {n} possible RENAME (consider sp_rename instead of drop+create)',
    noStructuralRisk: 'No structural change carries risk.',
    riskS1: '<b>{n}</b> tables have structural changes.',
    riskS2: '<b>{n}</b> are <b>full</b> in target → will stop during deployment ({loss} with data-loss risk).',
    riskS3: '<b>{n}</b> are risky but empty in target → apply cleanly.',
    riskS4: '<b>{n}</b> are low risk → applied in place.',
    riskS5: '<b>{n}</b> have an unreadable row count → check manually.',
    riskChipTotal: 'affected tables', riskChipBlock: 'will block', riskChipLoss: 'data loss',
    riskChipClean: 'apply cleanly', riskChipUnknown: 'check manually',
    rLabelDataLoss: 'DATA LOSS', rLabelBlock: 'WILL BLOCK', rLabelCheck: 'CHECK DATA', rLabelEmpty: 'empty table',
    rLabelRisky: 'RISKY (row count unreadable)', rLabelInPlace: 'in place', rLabelSafe: 'safe',
    rowsN: '{n} rows', rowsUnknown: '? rows',
    noTriggers: 'No triggers found.',
    triggerMismatch: '<b>{n}</b> triggers differ in state between the two environments — highlighted below.',
    thTrigger: 'Trigger', thTarget: 'Target', thSource: 'Source', stateOff: 'DISABLED', stateOn: 'enabled',
    loading: 'loading…', noDefinition: 'No definition found for this object.',
    noContent: 'No content.', notOnThisSide: 'Not present on this side.', missingSuffix: ' — missing',
    noDiff: 'no differences', diffOfTotal: '{i} / {n} differences',
    generatingScript: 'generating script…', scriptFailed: 'Could not generate script.',
    scriptIncluded: '{n} objects written ({sel} of selected)',
    scriptIncludedAll: 'Nothing selected — the script is empty',
    dataLossIncluded: '⚠ DATA-LOSS steps (drop column/table, narrowing) INCLUDED',
    stillGated: '{n} steps still gated', outOfScopeN: '{n} objects out of scope (sequence/synonym etc.)',
    skippedN: '{n} objects skipped', downloaded: '{file} downloaded · {parts}',
    optgText: 'Text / normalization', optgColumn: 'Columns & types', optgIndex: 'Indexes',
    optgObject: 'Objects & constraints', optgScope: 'Scope', optgScript: 'Script / deployment (affects generated SQL)',
    opt_blockDataLoss: 'Block on possible data loss', optn_blockDataLoss: 'Data-loss steps (drop column/table, narrowing) are left out of the generated script and only reported.',
    opt_dropNotInSource: 'Drop objects not in source', optn_dropNotInSource: 'Objects, indexes and constraints that exist in target but not in source get DROP statements.',
    opt_scriptValidateConstraints: 'Script validation for new constraints', optn_scriptValidateConstraints: 'New CHECK/FK use WITH CHECK (validate existing rows). Uncheck for WITH NOCHECK so they can be added to a populated table without validation.',
    opt_ignoreWhitespace: 'Ignore whitespace', optn_ignoreWhitespace: 'Indentation and line-break differences are not counted.',
    opt_ignoreComments: 'Ignore comments', optn_ignoreComments: 'An object that only differs in comments is treated as identical.',
    opt_ignoreKeywordCasing: 'Ignore keyword casing', optn_ignoreKeywordCasing: '"select" equals "SELECT"; identifiers and literals are unaffected.',
    opt_ignoreSemicolons: 'Ignore semicolon between statements', optn_ignoreSemicolons: 'Trailing ";" differences are not counted.',
    opt_ignoreAnsiNulls: 'Ignore ANSI NULLS', optn_ignoreAnsiNulls: 'SET ANSI_NULLS differences on modules/triggers are not counted.',
    opt_ignoreQuotedIdentifiers: 'Ignore quoted identifiers', optn_ignoreQuotedIdentifiers: 'SET QUOTED_IDENTIFIER differences are not counted.',
    opt_ignoreColumnOrder: 'Ignore column order', optn_ignoreColumnOrder: 'Columns are matched by name, not by position.',
    opt_ignoreCollation: 'Ignore column collation', optn_ignoreCollation: 'When the two servers have different default collations every text column looks changed; this suppresses that noise.',
    opt_ignoreIdentitySeed: 'Ignore identity seed', optn_ignoreIdentitySeed: 'IDENTITY seed (start) value is not counted; whether the column is an IDENTITY still is.',
    opt_ignoreIdentityIncrement: 'Ignore increment', optn_ignoreIdentityIncrement: 'IDENTITY increment (step) value is not counted.',
    opt_ignoreIndexPhysical: 'Ignore index options', optn_ignoreIndexPhysical: 'All physical storage options (fill factor, padding, ignore_dup_key) are not counted.',
    opt_ignoreFillFactor: 'Ignore fill factor', optn_ignoreFillFactor: 'Index fill factor differences are not counted.',
    opt_ignoreIndexPadding: 'Ignore index padding', optn_ignoreIndexPadding: 'Index PAD_INDEX differences are not counted.',
    opt_ignoreDataCompression: 'Ignore data compression', optn_ignoreDataCompression: 'DATA_COMPRESSION (ROW/PAGE) differences on indexes are not counted.',
    opt_ignoreStatistics: 'Ignore user statistics', optn_ignoreStatistics: 'User-created statistics (CREATE STATISTICS) are not compared. Auto-created ones never are.',
    opt_ignoreDmlTriggerState: 'Ignore DML trigger state', optn_ignoreDmlTriggerState: 'Whether a DML trigger is enabled/disabled is not counted.',
    opt_ignoreSystemNamedConstraints: 'Ignore system-named constraints', optn_ignoreSystemNamedConstraints: 'Auto names like PK__Tbl__A1B2C3 differ between environments.',
    opt_ignoreExtendedProperties: 'Ignore extended properties', optn_ignoreExtendedProperties: 'Descriptions like MS_Description are not compared. Default: compared (SSDT does too).',
    opt_ignorePermissions: 'Ignore users, roles and permissions', optn_ignorePermissions: 'Database users, user-defined roles, memberships and object/schema permissions are neither shown nor scripted — handled separately. Default: ignored.',
    opt_recreateChangedTableTypes: 'Recreate changed table types', optn_recreateChangedTableTypes: 'A changed table type cannot be altered. When on, dependent modules are dropped, the type is recreated and the modules are restored — only if every dependent definition is readable.',
    opt_caseSensitiveNames: 'Case-sensitive names', optn_caseSensitiveNames: "Default is case-insensitive — SQL Server's usual collation behaviour.",
  },
  tr: {
    source: 'Kaynak', target: 'Hedef', selectConnection: 'bağlantı seçin',
    swap: 'Kaynak ↔ Hedef', compare: 'Karşılaştır', generateScript: 'Script Üret',
    newComparison: 'Yeni karşılaştırma', newComparisonTip: 'Yeni karşılaştırmayı ayrı sekmede aç',
    reverseTip: 'Ters (geri alma) için işaretle — Generate Script\'te dahil edilir',
    reverseIncluded: '{n} obje ters bölümde',
    generateScriptTip: 'Seçili değişikliklerden dağıtım script\'i üret',
    options: 'Karşılaştırma seçenekleri', toggleTheme: 'Temayı değiştir', toggleLang: 'Dil',
    pickTwo: 'Karşılaştırmak için iki bağlantı seçin.', allTypes: 'Tüm türler',
    searchPh: 'şema veya obje ara…',
    tabDiff: 'Farklar', tabRisk: 'Deployment riski', tabTriggers: 'Trigger\'lar',
    type: 'Tür', objectDefinitions: 'Obje Tanımları',
    prevDiff: 'Önceki fark', nextDiff: 'Sonraki fark', canonicalText: 'kanonik metin',
    selectRow: 'Üstteki listeden bir satır seçin.', connect: 'Bağlan', recentConnections: 'Son bağlantılar',
    none: 'Kayıt yok.', connectionProperties: 'Bağlantı özellikleri', serverName: 'Sunucu adı',
    authentication: 'Kimlik doğrulama', username: 'Kullanıcı adı', password: 'Parola',
    rememberPassword: 'Parolayı hatırla', database: 'Veritabanı', listFirst: '— sunucu girin —',
    authWindows: 'Windows Kimlik Doğrulaması', authSql: 'SQL Server Kimlik Doğrulaması',
    encrypt: 'Şifreleme', encOptional: 'İsteğe bağlı (False)', encMandatory: 'Zorunlu (True)',
    trustTrue: 'Evet', trustFalse: 'Hayır',
    trustCert: 'Sunucu sertifikasına güven', testConnection: 'Bağlantıyı sına',
    cancel: 'İptal', comparisonOptions: 'Karşılaştırma seçenekleri', resetDefaults: 'Varsayılana dön', ok: 'Tamam',
    connectSource: 'Kaynak bağlantısı', connectTarget: 'Hedef bağlantısı',
    pwSaved: ' · parola kayıtlı', listUnreadable: 'Liste okunamadı.',
    storedPwHint: 'Kayıtlı parola sunucuda çözülecek — alanı boş bırakın.',
    enterServerFirst: 'Önce sunucu adını girin.', listingDatabases: 'veritabanları listeleniyor…',
    couldNotList: 'Listelenemedi.', selectOpt: '— seçin —', databasesFound: '{n} veritabanı bulundu.',
    testing: 'sınanıyor…', couldNotConnect: 'Bağlanılamadı.',
    connectedAs: 'Bağlandı — {server} ({version}), kullanıcı {login}',
    serverRequired: 'Sunucu adı zorunlu.', databaseRequired: 'Bir veritabanı seçin.',
    startingCompare: 'karşılaştırma başlatılıyor…', couldNotStart: 'Başlatılamadı.',
    comparing: '{source} → {target} karşılaştırılıyor…', connectionLost: 'Bağlantı koptu.',
    progressLine: '{source} → {target} · %{percent}{eta}',
    etaSuffix: ' · ~{sec} kaldı', etaCalc: ' · süre hesaplanıyor…',
    secShort: '{n}sn', minShort: '{n}dk',
    phaseConnecting: 'bağlanılıyor', phaseExtracting: 'şema okunuyor',
    phaseBuilding: 'model kuruluyor', phaseComparing: 'karşılaştırılıyor', phaseDone: 'tamamlanıyor',
    notComparedYet: 'Henüz karşılaştırma yapılmadı.', noDiffForFilters: 'Bu filtrelerle gösterilecek fark yok.',
    listLimited: 'Liste {n} kayıtla sınırlandı — daha fazlası var.',
    flagIndeterminate: 'BELİRSİZ',
    pickInfo: '{n} obje seçili · Shift+tık ile aralık seç',
    pickInfoAll: 'Hiçbiri seçili değil → script boş olur',
    generatedScript: 'Üretilen script', downloadSql: '.sql indir', copy: 'Kopyala',
    dirForward: 'kaynak → hedef', dirReverse: 'hedef → kaynak',
    emptyScriptInfo: 'Bu yönde değişiklik yok.',
    scriptReady: 'Script hazır — gözden geçir/düzenle, sonra indir', scriptCopied: 'Script panoya kopyalandı',
    sqlDownloaded: 'İndirildi: {file}', selectGroupTip: 'Tüm {g} objelerini seç / kaldır',
    statResult: '{source} → {target} · {objects} obje · {equal} aynı · +{add} ~{change} −{delete} · {ms} ms',
    renameWarn: ' · ⚠ {n} olası YENİDEN ADLANDIRMA (drop+create yerine sp_rename düşünün)',
    noStructuralRisk: 'Tablo yapısında risk taşıyan değişiklik yok.',
    riskS1: '<b>{n}</b> tabloda yapısal değişiklik var.',
    riskS2: '<b>{n}</b> tanesi hedefte <b>dolu</b> → deployment sırasında duracak ({loss} tanesinde veri kaybı riski).',
    riskS3: '<b>{n}</b> tanesi riskli ama hedefte boş → sorunsuz uygulanır.',
    riskS4: '<b>{n}</b> tanesi düşük riskli → yerinde uygulanır.',
    riskS5: '<b>{n}</b> tanesinin satır sayısı okunamadı → elle kontrol edin.',
    riskChipTotal: 'etkilenen tablo', riskChipBlock: 'bloklanır', riskChipLoss: 'veri kaybı',
    riskChipClean: 'sorunsuz', riskChipUnknown: 'elle kontrol',
    rLabelDataLoss: 'VERİ KAYBI', rLabelBlock: 'BLOKLANIR', rLabelCheck: 'KONTROL ET', rLabelEmpty: 'boş tablo',
    rLabelRisky: 'RİSKLİ (satır sayısı okunamadı)', rLabelInPlace: 'yerinde', rLabelSafe: 'güvenli',
    rowsN: '{n} satır', rowsUnknown: '? satır',
    noTriggers: 'Trigger bulunamadı.',
    triggerMismatch: '<b>{n}</b> trigger\'ın durumu iki ortamda farklı — aşağıda vurgulandı.',
    thTrigger: 'Trigger', thTarget: 'Hedef', thSource: 'Kaynak', stateOff: 'PASİF', stateOn: 'aktif',
    loading: 'yükleniyor…', noDefinition: 'Bu obje için tanım bulunamadı.',
    noContent: 'İçerik yok.', notOnThisSide: 'Bu tarafta yok.', missingSuffix: ' — yok',
    noDiff: 'fark yok', diffOfTotal: '{i} / {n} fark',
    generatingScript: 'script üretiliyor…', scriptFailed: 'Script üretilemedi.',
    scriptIncluded: '{n} obje script\'e girdi ({sel} seçiliden)',
    scriptIncludedAll: 'Hiçbiri seçili değil — script boş',
    dataLossIncluded: '⚠ VERİ KAYBI adımları (kolon/tablo silme, tip daraltma) DAHİL',
    stillGated: '{n} adım yine de gated', outOfScopeN: '{n} obje kapsam dışı (sequence/synonym vb.)',
    skippedN: '{n} obje atlandı', downloaded: '{file} indirildi · {parts}',
    optgText: 'Metin / normalizasyon', optgColumn: 'Kolon ve tipler', optgIndex: 'Index\'ler',
    optgObject: 'Nesne ve constraint\'ler', optgScope: 'Kapsam', optgScript: 'Script / dağıtım (üretilen SQL\'e etki eder)',
    opt_blockDataLoss: 'Olası veri kaybında durdur', optn_blockDataLoss: 'Veri kaybı adımları (kolon/tablo silme, tip daraltma) üretilen script\'e girmez, yalnızca raporlanır.',
    opt_dropNotInSource: 'Kaynakta olmayan nesneleri sil', optn_dropNotInSource: 'Hedefte olup kaynakta olmayan nesne, index ve constraint\'ler için DROP üretilir.',
    opt_scriptValidateConstraints: 'Yeni constraint\'leri doğrula', optn_scriptValidateConstraints: 'Yeni CHECK/FK WITH CHECK ile üretilir (mevcut satırları doğrular). Kapatınca WITH NOCHECK — dolu tabloya doğrulamadan eklenebilir.',
    opt_ignoreWhitespace: 'Boşlukları yok say', optn_ignoreWhitespace: 'Girinti ve satır sonu farkları fark sayılmaz.',
    opt_ignoreComments: 'Yorumları yok say', optn_ignoreComments: 'Yalnızca yorumu değişen obje "aynı" sayılır.',
    opt_ignoreKeywordCasing: 'Anahtar kelime büyük/küçük harfini yok say', optn_ignoreKeywordCasing: '"select" ile "SELECT" aynı sayılır; tanımlayıcılar ve literaller etkilenmez.',
    opt_ignoreSemicolons: 'İfadeler arası noktalı virgülü yok say', optn_ignoreSemicolons: 'İfade sonundaki ";" farkları fark sayılmaz.',
    opt_ignoreAnsiNulls: 'ANSI NULLS\'u yok say', optn_ignoreAnsiNulls: 'Modül/trigger\'larda SET ANSI_NULLS farkı fark sayılmaz.',
    opt_ignoreQuotedIdentifiers: 'Quoted identifier\'ları yok say', optn_ignoreQuotedIdentifiers: 'SET QUOTED_IDENTIFIER farkı fark sayılmaz.',
    opt_ignoreColumnOrder: 'Kolon sırasını yok say', optn_ignoreColumnOrder: 'Kolonlar tanım sırasına değil ada göre eşleştirilir.',
    opt_ignoreCollation: 'Kolon collation\'ını yok say', optn_ignoreCollation: 'İki sunucunun varsayılan collation\'ı farklıysa her metin kolonu fark görünür; bu gürültüyü bastırır.',
    opt_ignoreIdentitySeed: 'IDENTITY seed\'ini yok say', optn_ignoreIdentitySeed: 'IDENTITY başlangıç değeri fark sayılmaz; kolonun IDENTITY olup olmadığı yine karşılaştırılır.',
    opt_ignoreIdentityIncrement: 'IDENTITY increment\'ini yok say', optn_ignoreIdentityIncrement: 'IDENTITY artış (adım) değeri fark sayılmaz.',
    opt_ignoreIndexPhysical: 'Index seçeneklerini yok say', optn_ignoreIndexPhysical: 'Tüm fiziksel depolama ayarları (fill factor, padding, ignore_dup_key) fark sayılmaz.',
    opt_ignoreFillFactor: 'Fill factor\'ı yok say', optn_ignoreFillFactor: 'Index fill factor farkı fark sayılmaz.',
    opt_ignoreIndexPadding: 'Index padding\'ini yok say', optn_ignoreIndexPadding: 'Index PAD_INDEX farkı fark sayılmaz.',
    opt_ignoreDataCompression: 'Veri sıkıştırmayı yok say', optn_ignoreDataCompression: 'Index DATA_COMPRESSION (ROW/PAGE) farkı fark sayılmaz.',
    opt_ignoreStatistics: 'Kullanıcı istatistiklerini yok say', optn_ignoreStatistics: 'CREATE STATISTICS ile oluşturulan istatistikler karşılaştırılmaz. Otomatik üretilenler zaten kapsam dışıdır.',
    opt_ignoreDmlTriggerState: 'DML trigger durumunu yok say', optn_ignoreDmlTriggerState: 'DML trigger\'ın etkin/pasif olması fark sayılmaz.',
    opt_ignoreSystemNamedConstraints: 'Sistem üretimi constraint adlarını yok say', optn_ignoreSystemNamedConstraints: 'PK__Tbl__A1B2C3 gibi otomatik adlar ortamlar arasında farklıdır.',
    opt_ignoreExtendedProperties: 'Extended property\'leri yok say', optn_ignoreExtendedProperties: 'MS_Description gibi açıklamalar karşılaştırılmaz. Varsayılan: karşılaştırılır (SSDT de eder).',
    opt_ignorePermissions: 'Kullanıcı, rol ve izinleri yok say', optn_ignorePermissions: 'Veritabanı kullanıcıları, roller, üyelikler ve obje/şema izinleri ne ekranda gösterilir ne de script\'e girer — ayrıca ele alınır. Varsayılan: yok sayılır.',
    opt_recreateChangedTableTypes: 'Değişen table type\'ları yeniden kur', optn_recreateChangedTableTypes: 'Table type ALTER edilemez. Açıkken bağımlı modüller düşürülür, tip yeniden kurulur ve modüller geri yüklenir — yalnızca bağımlıların TAMAMININ tanımı okunabiliyorsa.',
    opt_caseSensitiveNames: 'İsimlerde büyük/küçük harf duyarlı', optn_caseSensitiveNames: 'Varsayılan duyarsızdır — SQL Server\'ın olağan collation davranışı.',
  },
};

function t(key, vars) {
  let s = (I18N[LANG] && I18N[LANG][key]) ?? I18N.en[key] ?? key;
  if (vars) for (const k in vars) s = s.replaceAll(`{${k}}`, vars[k]);
  return s;
}

function applyI18n() {
  document.documentElement.lang = LANG;
  for (const el of document.querySelectorAll('[data-i18n]')) el.textContent = t(el.dataset.i18n);
  for (const el of document.querySelectorAll('[data-i18n-title]')) el.title = t(el.dataset.i18nTitle);
  for (const el of document.querySelectorAll('[data-i18n-ph]')) el.placeholder = t(el.dataset.i18nPh);
  $('langLabel').textContent = LANG === 'en' ? 'TR' : 'EN';
}

function applyTheme() {
  document.documentElement.dataset.theme = THEME;
  $('themeBtn').textContent = THEME === 'dark' ? '☀' : '☾';
}

// ================= T-SQL renklendirme =================

const SQL_KEYWORDS = new Set(('ADD ALL ALTER AND ANY APPLY AS ASC AFTER AUTHORIZATION BEGIN BETWEEN BREAK BY ' +
  'CASCADE CASE CATCH CHECK CLUSTERED COLLATE COLUMN COMMIT COMPUTED CONSTRAINT CONTINUE CREATE CROSS DATABASE ' +
  'DECLARE DEFAULT DELETE DENY DESC DISABLE DISTINCT DROP ELSE ENABLE END EXCEPT EXEC EXECUTE EXISTS EXTERNAL ' +
  'FILEGROUP FILLFACTOR FOR FOREIGN FROM FULL FUNCTION GENERATED GO GRANT GROUP HAVING HIDDEN IDENTITY IF IN ' +
  'INCLUDE INDEX INNER INSERT INSTEAD INTERSECT INTO IS JOIN KEY LEFT LIKE MASKED MEMBER MERGE NO NOCHECK ' +
  'NOCOUNT NOEXEC NONCLUSTERED NONE NOT NULL OF OFF ON ONLINE OR ORDER OUTER OUTPUT OVER PARTITION PERIOD ' +
  'PERSISTED PIVOT PRIMARY PRINT PROC PROCEDURE RANGE REBUILD REFERENCES RESTRICT RETURN RETURNS REVOKE RIGHT ' +
  'ROLE ROLLBACK ROWGUIDCOL SCHEMA SCHEME SELECT SEQUENCE SET SPARSE SYNONYM SYSTEM_VERSIONING TABLE THEN THROW ' +
  'TO TOP TRAN TRANSACTION TRIGGER TRY TYPE UNION UNIQUE UNPIVOT UPDATE USE USING VALUES VIEW WHEN WHERE WHILE ' +
  'WITH ANSI_NULLS ANSI_PADDING ANSI_WARNINGS ARITHABORT CONCAT_NULL_YIELDS_NULL NUMERIC_ROUNDABORT ' +
  'QUOTED_IDENTIFIER XACT_ABORT PAD_INDEX DATA_COMPRESSION IGNORE_DUP_KEY').split(' '));

const SQL_TYPES = new Set(('BIGINT BINARY BIT CHAR DATE DATETIME DATETIME2 DATETIMEOFFSET DECIMAL FLOAT GEOGRAPHY ' +
  'GEOMETRY HIERARCHYID IMAGE INT MAX MONEY NCHAR NTEXT NUMERIC NVARCHAR REAL ROWVERSION SMALLDATETIME SMALLINT ' +
  'SMALLMONEY SQL_VARIANT SYSNAME TEXT TIME TIMESTAMP TINYINT UNIQUEIDENTIFIER VARBINARY VARCHAR XML').split(' '));

const SQL_FUNCTIONS = new Set(('ABS AVG CAST CEILING CHARINDEX CHECKSUM COALESCE COL_LENGTH CONCAT CONVERT COUNT ' +
  'DATEADD DATEDIFF DATEPART DAY DB_ID DB_NAME FLOOR FORMAT GETDATE GETUTCDATE HASHBYTES IIF ISNULL LEN LOWER ' +
  'LTRIM MIN MONTH NEWID NULLIF OBJECT_ID OBJECTPROPERTY PATINDEX REPLACE ROUND ROW_NUMBER RTRIM SCHEMA_ID ' +
  'SERVERPROPERTY STR STUFF SUBSTRING SUM SUSER_NAME SUSER_SNAME SYSDATETIME SYSUTCDATETIME TRIM TRY_CAST ' +
  'TRY_CONVERT UPPER USER_NAME YEAR').split(' '));

const tag = (cls, text) => `<span class="t-${cls}">${esc(text)}</span>`;

/**
 * Satır satır renklendirir. Blok yorumlar satır aşarsa durum bir sonraki satıra
 * taşınır — tek satır tek satır bakan bir renklendirici /* ... *​/ arasında bozulurdu.
 */
function highlightLines(lines) {
  let inBlockComment = false;

  return lines.map((line) => {
    let out = '';
    let i = 0;

    while (i < line.length) {
      if (inBlockComment) {
        const end = line.indexOf('*/', i);
        if (end === -1) { out += tag('cmt', line.slice(i)); break; }
        out += tag('cmt', line.slice(i, end + 2));
        i = end + 2;
        inBlockComment = false;
        continue;
      }

      const rest = line.slice(i);
      let match;

      if ((match = /^--.*/.exec(rest))) { out += tag('cmt', match[0]); break; }

      if (rest.startsWith('/*')) {
        const end = line.indexOf('*/', i + 2);
        if (end === -1) { out += tag('cmt', rest); inBlockComment = true; break; }
        out += tag('cmt', line.slice(i, end + 2));
        i = end + 2;
        continue;
      }

      if ((match = /^N?'(?:''|[^'])*'/.exec(rest))) { out += tag('str', match[0]); i += match[0].length; continue; }
      if ((match = /^\[[^\]]*\]/.exec(rest))) { out += tag('id', match[0]); i += match[0].length; continue; }
      if ((match = /^@@?[A-Za-z_]\w*/.exec(rest))) { out += tag('var', match[0]); i += match[0].length; continue; }
      if ((match = /^(?:0x[0-9A-Fa-f]+|\d+(?:\.\d+)?)/.exec(rest))) { out += tag('num', match[0]); i += match[0].length; continue; }

      if ((match = /^[A-Za-z_]\w*/.exec(rest))) {
        const upper = match[0].toUpperCase();
        const cls = SQL_KEYWORDS.has(upper) ? 'kw'
          : SQL_TYPES.has(upper) ? 'typ'
          : SQL_FUNCTIONS.has(upper) ? 'fn' : null;
        out += cls ? tag(cls, match[0]) : esc(match[0]);
        i += match[0].length;
        continue;
      }

      if ((match = /^\s+/.exec(rest))) { out += esc(match[0]); i += match[0].length; continue; }

      out += tag('op', line[i]);
      i += 1;
    }

    return out;
  });
}

// SSDT'nin sonuç ağacındaki sıra: önce silinecekler, sonra değişenler, sonra eklenecekler.
const GROUPS = [
  { action: 'Delete', label: 'Delete', cls: 'delete', mark: '−' },
  { action: 'Change', label: 'Change', cls: 'change', mark: '~' },
  { action: 'Add', label: 'Add', cls: 'add', mark: '+' },
];

// SSDT'nin "General" sekmesindeki seçenekleri gruplayarak yansıtır. Yalnızca bu araçta
// GERÇEKTEN uygulanan seçenekler var — çalışmayan bir kutu göstermek yanıltıcı olur.
const OPTION_GROUPS = [
  { group: 'optgText', keys: ['ignoreWhitespace', 'ignoreComments', 'ignoreKeywordCasing',
                              'ignoreSemicolons', 'ignoreAnsiNulls', 'ignoreQuotedIdentifiers'] },
  { group: 'optgColumn', keys: ['ignoreColumnOrder', 'ignoreCollation',
                                'ignoreIdentitySeed', 'ignoreIdentityIncrement'] },
  { group: 'optgIndex', keys: ['ignoreIndexPhysical', 'ignoreFillFactor', 'ignoreIndexPadding',
                               'ignoreDataCompression', 'ignoreStatistics'] },
  { group: 'optgObject', keys: ['ignoreDmlTriggerState', 'ignoreSystemNamedConstraints'] },
  { group: 'optgScope', keys: ['ignoreExtendedProperties', 'ignorePermissions', 'caseSensitiveNames'] },
];

const OPTION_KEYS = OPTION_GROUPS.flatMap((g) => g.keys);

const DEFAULT_OPTIONS = {
  ignoreWhitespace: true, ignoreComments: true, ignoreKeywordCasing: false,
  ignoreSemicolons: false, ignoreAnsiNulls: true, ignoreQuotedIdentifiers: true,
  ignoreColumnOrder: false, ignoreCollation: false,
  ignoreIdentitySeed: false, ignoreIdentityIncrement: false,
  ignoreIndexPhysical: false, ignoreFillFactor: true, ignoreIndexPadding: false, ignoreDataCompression: false,
  ignoreStatistics: false,
  ignoreDmlTriggerState: false, ignoreSystemNamedConstraints: true,
  ignoreExtendedProperties: false, ignorePermissions: true, caseSensitiveNames: false,
  blockDataLoss: false, dropNotInSource: true, scriptValidateConstraints: true,
  recreateChangedTableTypes: false,
  maxQueries: 16,
};

const state = {
  source: null,
  target: null,
  options: { ...DEFAULT_OPTIONS },
  runId: null,
  result: null,
  selected: null,
  detail: null,
  editing: null,
  expanded: new Set(),      // açılmış objeler
  collapsed: new Set(),     // kapatılmış üst gruplar
  collapsedCats: new Set(), // kapatılmış kategori klasörleri (varsayılan açık)
  checked: new Set(),       // işaretlenmiş satırlar (ileri yön)
  reversed: new Set(),      // ⇄ ile işaretlenen objeler (geri alma / ters yön)
  lastPick: null,           // shift+tık aralık seçimi için son tıklanan kutu (çapa)
  eventSource: null,
  hunks: [],                // fark bloklarının başladığı satır indeksleri
  hunkIndex: -1,
  diffRowCount: 0,          // alt panelde toplam satır (minimap konumlaması için)
  wordTerm: '',             // alt panelde sarı vurgulanan kelime (toggle için)
  optCollapsed: new Set(),  // ayarlar menüsünde kapatılmış kategori başlıkları
};

// Ağaçtaki klasör sırası — SSDT'nin gösterdiği sırayla aynı.
const CATEGORY_ORDER = ['Columns', 'Primary Key', 'Unique Constraints', 'Indexes',
                        'Foreign Keys', 'Check Constraints', 'Properties'];

const ACTION_ICON = { Add: '＋', Change: '✎', Delete: '✕' };

// ================= bağlantı diyaloğu =================

function openConnect(which) {
  state.editing = which;
  const existing = state[which];

  $('connTitle').textContent = t(which === 'source' ? 'connectSource' : 'connectTarget');
  $('cServer').value = existing?.server ?? '';
  $('cAuth').value = existing?.authentication ?? 'Windows';
  $('cUser').value = existing?.userName ?? '';
  $('cPass').value = existing?.password ?? '';
  $('cRemember').checked = existing?.rememberPassword ?? false;
  $('cEncrypt').value = String(existing?.encrypt ?? false);
  $('cTrust').value = String(existing?.trustServerCertificate ?? true);
  $('cDatabase').innerHTML = existing?.database
    ? `<option value="${esc(existing.database)}">${esc(existing.database)}</option>`
    : `<option value="">${esc(t('listFirst'))}</option>`;
  $('connStatus').textContent = '';
  $('connStatus').className = 'conn-status';
  $('cDatabase').dataset.recentId = existing?.id ?? '';

  dbLoadedSig = null;   // yeni diyalog → veritabanı listesi guard'ını sıfırla (ilk tıklamada taze getir)
  syncAuthRows();
  loadRecent();

  $('connScrim').hidden = false;
  $('connDialog').hidden = false;
  $('cServer').focus();
}

function closeConnect() {
  $('connScrim').hidden = true;
  $('connDialog').hidden = true;
  state.editing = null;
}

function syncAuthRows() {
  const isSql = $('cAuth').value === 'SqlLogin';
  for (const id of ['rowUser', 'rowPass', 'rowRemember']) $(id).classList.toggle('hidden', !isSql);
}

function readDialogConnection() {
  return {
    server: $('cServer').value.trim(),
    database: $('cDatabase').value || null,
    authentication: $('cAuth').value,
    userName: $('cUser').value.trim() || null,
    password: $('cPass').value || null,
    rememberPassword: $('cRemember').checked,
    encrypt: $('cEncrypt').value === 'true',
    trustServerCertificate: $('cTrust').value === 'true',
    id: $('cDatabase').dataset.recentId || null,
  };
}

async function loadRecent() {
  const list = $('recentList');
  try {
    const items = await (await fetch('/api/connections/recent')).json();
    if (!items.length) { list.innerHTML = `<p class="hint">${esc(t('none'))}</p>`; return; }

    list.innerHTML = items.map((r) => `
      <div class="recent-item" data-id="${esc(r.id)}">
        <div class="recent-main">
          <div class="recent-label">${esc(r.label)}</div>
          <div class="recent-sub">${esc(r.authentication === 'SqlLogin' ? (r.userName ?? 'SQL') : 'Windows')}${r.hasStoredPassword ? esc(t('pwSaved')) : ''}</div>
        </div>
        <button class="recent-x" data-forget="${esc(r.id)}">✕</button>
      </div>`).join('');

    for (const el of list.querySelectorAll('.recent-item')) {
      el.addEventListener('click', (e) => {
        if (e.target.dataset.forget) return;
        applyRecent(items.find((i) => i.id === el.dataset.id));
      });
    }
    for (const btn of list.querySelectorAll('[data-forget]')) {
      btn.addEventListener('click', async (e) => {
        e.stopPropagation();
        await fetch(`/api/connections/recent/${btn.dataset.forget}`, { method: 'DELETE' });
        loadRecent();
      });
    }
  } catch {
    list.innerHTML = `<p class="hint">${esc(t('listUnreadable'))}</p>`;
  }
}

function applyRecent(item) {
  if (!item) return;
  $('cServer').value = item.server;
  $('cAuth').value = item.authentication;
  $('cUser').value = item.userName ?? '';
  $('cPass').value = '';                       // parola tarayıcıya hiç gönderilmez
  $('cRemember').checked = item.hasStoredPassword;
  $('cEncrypt').value = String(item.encrypt);
  $('cTrust').value = String(item.trustServerCertificate);
  $('cDatabase').innerHTML = item.database
    ? `<option value="${esc(item.database)}">${esc(item.database)}</option>`
    : `<option value="">${esc(t('listFirst'))}</option>`;
  $('cDatabase').dataset.recentId = item.id;
  syncAuthRows();
  setConnStatus(item.hasStoredPassword ? t('storedPwHint') : '');
}

function setConnStatus(text, kind = '') {
  const el = $('connStatus');
  el.textContent = text;
  el.className = `conn-status ${kind}`;
}

async function loadDatabases() {
  const connection = readDialogConnection();
  if (!connection.server) { setConnStatus(t('enterServerFirst'), 'error'); return false; }

  setConnStatus(t('listingDatabases'));
  try {
    const response = await fetch('/api/connections/databases', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(connection),
    });
    const data = await response.json();
    if (!response.ok) { setConnStatus(data.error ?? t('couldNotList'), 'error'); return false; }

    const current = $('cDatabase').value;
    $('cDatabase').innerHTML = `<option value="">${esc(t('selectOpt'))}</option>` +
      data.map((d) => `<option value="${esc(d)}" ${d === current ? 'selected' : ''}>${esc(d)}</option>`).join('');
    setConnStatus(t('databasesFound', { n: data.length }), 'ok');
    return true;
  } catch (error) {
    setConnStatus(error.message, 'error');
    return false;
  }
}

async function testConnection() {
  setConnStatus(t('testing'));
  try {
    const response = await fetch('/api/connections/test', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(readDialogConnection()),
    });
    const probe = await response.json();
    if (!response.ok) { setConnStatus(probe.error ?? t('couldNotConnect'), 'error'); return; }
    setConnStatus(probe.ok
      ? t('connectedAs', { server: probe.serverName, version: probe.version, login: probe.loginName })
      : probe.error, probe.ok ? 'ok' : 'error');
  } catch (error) {
    setConnStatus(error.message, 'error');
  }
}

function confirmConnection() {
  const connection = readDialogConnection();
  if (!connection.server) { setConnStatus(t('serverRequired'), 'error'); return; }
  if (!connection.database) { setConnStatus(t('databaseRequired'), 'error'); return; }

  state[state.editing] = connection;
  renderEndpoints();
  closeConnect();
}

function renderEndpoints() {
  for (const which of ['source', 'target']) {
    const connection = state[which];
    const button = $(which === 'source' ? 'sourceBtn' : 'targetBtn');
    const value = $(which === 'source' ? 'sourceValue' : 'targetValue');
    button.classList.toggle('unset', !connection);
    value.textContent = connection ? `${connection.server}.${connection.database}` : t('selectConnection');
  }
  $('compareBtn').disabled = !(state.source && state.target);
}

// ================= seçenekler =================

function renderOptions() {
  // Üst kategoriler açılır/kapanır (accordion). Her seçenek TEK satır; açıklama üstüne
  // gelince (title tooltip) detaylı çıkar. Aktif (varsayılandan farklı) seçenek sayısı rozette.
  $('optList').innerHTML = OPTION_GROUPS.map((g) => {
    const collapsed = state.optCollapsed.has(g.group);
    const activeCount = g.keys.filter((k) => state.options[k] !== DEFAULT_OPTIONS[k]).length;
    const header = `
      <div class="opt-group${collapsed ? ' collapsed' : ''}" data-group="${g.group}">
        <span class="caret">${collapsed ? '▸' : '▾'}</span>
        <span class="opt-group-name">${esc(t(g.group))}</span>
        ${activeCount ? `<span class="opt-group-badge">${activeCount}</span>` : ''}
      </div>`;
    if (collapsed) return header;
    const rows = g.keys.map((key) => `
      <label class="opt-row" title="${esc(t('optn_' + key))}">
        <input type="checkbox" data-opt="${key}" ${state.options[key] ? 'checked' : ''}>
        <span class="opt-label">${esc(t('opt_' + key))}</span>
      </label>`).join('');
    return header + rows;
  }).join('');

  // Başlığa tıkla → o kategoriyi aç/kapa.
  for (const h of $('optList').querySelectorAll('.opt-group'))
    h.addEventListener('click', () => {
      const grp = h.dataset.group;
      state.optCollapsed.has(grp) ? state.optCollapsed.delete(grp) : state.optCollapsed.add(grp);
      renderOptions();
    });

  for (const box of $('optList').querySelectorAll('[data-opt]'))
    box.addEventListener('change', (e) => { e.stopPropagation(); state.options[box.dataset.opt] = box.checked; renderOptions(); });
}

// ================= karşılaştırma =================

async function compare() {
  $('compareBtn').disabled = true;
  setStatus(t('startingCompare'), 'busy');
  state.eventSource?.close();
  state.result = null;
  state.selected = null;
  state.checked.clear();
  state.reversed.clear();
  state.lastPick = null;
  renderAll();

  let data;
  try {
    const response = await fetch('/api/compare', {
      method: 'POST', headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ source: state.source, target: state.target, options: state.options }),
    });
    data = await response.json();
    if (!response.ok) throw new Error(data.error ?? t('couldNotStart'));
  } catch (error) {
    setStatus(error.message, 'error');
    $('compareBtn').disabled = false;
    return;
  }

  state.runId = data.runId;
  setStatus(t('comparing', { source: data.source, target: data.target }), 'busy');

  const source = new EventSource(`/api/runs/${data.runId}/events`);
  state.eventSource = source;
  let done = false;

  source.addEventListener('progress', (event) => {
    if (done) return;
    try { setProgress(JSON.parse(event.data)); } catch { /* yut */ }
  });

  source.addEventListener('result', (event) => {
    done = true;
    source.close();
    state.result = JSON.parse(event.data);
    selectAllChanges();   // ilk başta hepsi seçili gelsin
    $('scriptBtn').disabled = false;
    renderAll();
    const r = state.result;
    const renameCount = (r.renames || []).length;
    let msg = t('statResult', {
      source: r.sourceLabel, target: r.targetLabel, objects: num(r.sourceObjects),
      equal: num(r.equal), add: num(r.addCount), change: num(r.changeCount),
      delete: num(r.deleteCount), ms: num(Math.round(r.durationMs)),
    });
    if (renameCount > 0) msg += t('renameWarn', { n: num(renameCount) });
    setStatus(msg, renameCount > 0 ? 'error' : '');
    $('compareBtn').disabled = false;
  });

  source.addEventListener('failed', (event) => {
    done = true;
    source.close();
    setStatus(JSON.parse(event.data).error, 'error');
    $('compareBtn').disabled = false;
  });

  // Sunucu akışı normal kapattığında da 'error' tetiklenir; biteni hata sanmayalım.
  source.onerror = () => {
    if (done) return;
    source.close();
    setStatus(t('connectionLost'), 'error');
    $('compareBtn').disabled = false;
  };
}

function setStatus(text, kind = '') {
  const bar = $('statusbar');
  bar.textContent = text;
  bar.className = `statusbar ${kind}`;
}

// Saniyeyi okunur süreye çevirir: "8sn", "1dk 20sn", "2dk".
function formatDuration(totalSeconds) {
  const s = Math.max(0, Math.round(totalSeconds));
  if (s < 60) return t('secShort', { n: s });
  const m = Math.floor(s / 60);
  const rem = s % 60;
  return rem === 0 ? t('minShort', { n: m }) : `${t('minShort', { n: m })} ${t('secShort', { n: rem })}`;
}

// Compare sırasında ilerleme çubuğunu çizer (yüzde + kalan süre + aşama).
function setProgress(data) {
  const bar = $('statusbar');
  const percent = Math.max(0, Math.min(100, data.percent ?? 0));
  const phaseKey = 'phase' + (data.phase ? data.phase[0].toUpperCase() + data.phase.slice(1) : 'Connecting');
  const phaseLabel = I18N[LANG][phaseKey] || '';
  const eta = data.etaSeconds != null
    ? t('etaSuffix', { sec: formatDuration(data.etaSeconds) })
    : (percent >= 10 && percent < 100 ? t('etaCalc') : '');
  const line = t('progressLine', { source: data.source, target: data.target, percent, eta });
  bar.className = 'statusbar busy';
  bar.innerHTML =
    `<div class="progress">` +
    `<div class="progress-track"><div class="progress-fill" style="width:${percent}%"></div></div>` +
    `<span class="progress-text">${esc(line)}${phaseLabel ? ' · ' + esc(phaseLabel) : ''}</span>` +
    `</div>`;
}

// ================= ağaç =================

function visibleChanges() {
  if (!state.result) return [];
  const term = $('search').value.trim().toLowerCase();
  const type = $('typeFilter').value;

  return state.result.changes.filter((c) =>
    (!type || c.objectType === type) &&
    (!term || `${c.schema}.${c.name}`.toLowerCase().includes(term)));
}

function renderTree() {
  const tree = $('tree');
  if (!state.result) {
    tree.innerHTML = `<p class="missing">${esc(t('notComparedYet'))}</p>`;
    return;
  }

  const changes = visibleChanges();
  if (changes.length === 0) {
    tree.innerHTML = `<p class="missing">${esc(t('noDiffForFilters'))}</p>`;
    return;
  }

  let html = '';
  for (const group of GROUPS) {
    const rows = changes.filter((c) => c.action === group.action);
    if (rows.length === 0) continue;

    const collapsed = state.collapsed.has(group.action);
    const allChecked = rows.every((c) => objIncluded(`${c.objectType}|${c.schema}|${c.name}`));
    html += `<div class="group ${group.cls}" data-group="${group.action}">
      <span class="c-type">
        <span class="caret">${collapsed ? '▸' : '▾'}</span>
        <span class="gname">${group.label}</span>
        <span class="gcount">${num(rows.length)}</span>
      </span>
      <span></span>
      <span class="c-mid"><input type="checkbox" class="pick gpick" data-gpick="${group.action}"
        ${allChecked ? 'checked' : ''} title="${esc(t('selectGroupTip', { g: group.label }))}"></span>
      <span></span>
    </div>`;
    if (collapsed) continue;

    for (const change of rows) {
      const objKey = `${change.objectType}|${change.schema}|${change.name}`;
      html += objectRow(change, objKey);

      if (!state.expanded.has(objKey) || change.children.length === 0) continue;

      for (const category of orderedCategories(change.children)) {
        const items = change.children.filter((c) => c.category === category);
        const catKey = `${objKey}›${category}`;
        const catOpen = !state.collapsedCats.has(catKey);
        // Kategori tamamen tik-dışıysa (obje de tikli değil) üstü çizili göster.
        const catIn = state.checked.has(objKey) ||
          items.some((it) => state.checked.has(`${catKey}›${it.name}`));
        const struck = selectionActive() && !catIn ? ' struck' : '';

        html += `<div class="row-cat${struck}" data-cat="${esc(catKey)}">
          <span class="c-type" style="padding-left:26px">
            <span class="caret">${catOpen ? '▾' : '▸'}</span>
            <span class="folder">${esc(category)}</span>
            <span class="catcount">${items.length}</span>
          </span><span></span><span></span><span></span>
        </div>`;

        if (catOpen) for (const item of items) html += childRow(change, objKey, catKey, item);
      }
    }
  }

  if (state.result.truncated)
    html += `<p class="missing">${esc(t('listLimited', { n: num(state.result.changes.length) }))}</p>`;

  // Tik değişiminde tam re-render yapıyoruz (dim/struck güncellensin); scroll'u koru.
  const wrap = $('treeWrap');
  const scroll = wrap ? wrap.scrollTop : 0;
  tree.innerHTML = html;
  bindTree(tree);
  renderPickInfo();
  if (wrap) wrap.scrollTop = scroll;
}

function orderedCategories(children) {
  const present = new Set(children.map((c) => c.category));
  const known = CATEGORY_ORDER.filter((c) => present.has(c));
  const extra = [...present].filter((c) => !CATEGORY_ORDER.includes(c)).sort();
  return [...known, ...extra];
}

// Seçim modu: en az bir tik varsa. Bu modda script'e GİRMEYECEK satırlar soluk gösterilir.
function selectionActive() { return state.checked.size > 0; }

// Obje script'e girecek mi: kendisi tikli ya da altındaki bir öğe tikli.
function objIncluded(objKey) {
  if (state.checked.has(objKey)) return true;
  for (const k of state.checked) if (k.startsWith(objKey + '›')) return true;
  return false;
}

// objKey ('Type|schema|name') → değişiklik objesi.
function findChange(objKey) {
  const p = objKey.split('|');
  const type = p[0], schema = p[1], name = p.slice(2).join('|');
  return (state.result?.changes ?? []).find((c) => c.objectType === type && c.schema === schema && c.name === name);
}

// Bir tiki ayarla. Obje-seviyesi tikse (içinde › yok) tüm alt öğelerini de aynı duruma getir.
function setPick(key, on) {
  if (!key) return;   // güvenlik: geçersiz anahtar state'i bozmasın
  on ? state.checked.add(key) : state.checked.delete(key);
  if (!key.includes('›')) {
    for (const ch of findChange(key)?.children ?? []) {
      const ck = `${key}›${ch.category}›${ch.name}`;
      on ? state.checked.add(ck) : state.checked.delete(ck);
    }
  }
}

function objectRow(change, objKey) {
  // Şema objelerinde schema=name (çift yazım olur); şemasız objelerde (rol, db-seviyesi)
  // schema boştur. İkisinde de yalnızca adı göster.
  const full = (!change.schema || change.objectType === 'Schema')
    ? change.name : `${change.schema}.${change.name}`;
  const dim = selectionActive() && !objIncluded(objKey) ? ' dim' : '';
  const open = state.expanded.has(objKey);
  const expandable = change.children.length > 0;
  const selected = state.selected === objKey ? ' selected' : '';

  const flag = change.indeterminate ? `<span class="flag warn">${esc(t('flagIndeterminate'))}</span>` : '';

  return `<div class="row-obj${selected}${dim}" data-key="${esc(objKey)}"
      data-schema="${esc(change.schema)}" data-name="${esc(change.name)}" data-kind="${esc(change.objectType)}">
    <span class="c-type" style="padding-left:8px">
      <span class="caret" data-toggle="${esc(objKey)}">${expandable ? (open ? '▾' : '▸') : ''}</span>
      <span class="otype">${esc(change.objectType)}</span>
    </span>
    <span class="c-name">${change.action === 'Delete' ? '' : esc(full)}</span>
    <span class="c-mid">
      <input type="checkbox" class="pick" data-pick="${esc(objKey)}" title="Shift+tık: aralığı toplu seç/kaldır" ${state.checked.has(objKey) ? 'checked' : ''}>
      <span class="act ${change.action}">${ACTION_ICON[change.action]}</span>
    </span>
    <span class="c-name">${change.action === 'Add' ? '' : esc(full)}${flag}</span>
    ${state.checked.has(objKey)
      ? `<button class="rev${state.reversed.has(objKey) ? ' on' : ''}" data-rev="${esc(objKey)}" title="${esc(t('reverseTip'))}" aria-label="${esc(t('reverseTip'))}">⇄</button>`
      : ''}
  </div>`;
}

function childRow(change, objKey, catKey, item) {
  const pickKey = `${catKey}›${item.name}`;
  const name = item.qualifiedName;
  const detail = item.detail ? `<span class="child-detail">${esc(item.detail)}</span>` : '';
  // Ne kendisi ne parent obje tikli değilse soluk (obje tikliyse tüm alt öğeleri girer).
  const dim = selectionActive() && !(state.checked.has(objKey) || state.checked.has(pickKey)) ? ' dim' : '';

  return `<div class="row-child${dim}" data-key="${esc(objKey)}"
      data-schema="${esc(change.schema)}" data-name="${esc(change.name)}" data-kind="${esc(change.objectType)}">
    <span class="c-type" style="padding-left:52px">${esc(item.itemType)}</span>
    <span class="c-name">${item.action === 'Delete' ? '' : esc(name)}</span>
    <span class="c-mid">
      <input type="checkbox" class="pick" data-pick="${esc(pickKey)}" title="Shift+tık: aralığı toplu seç/kaldır" ${state.checked.has(pickKey) ? 'checked' : ''}>
      <span class="act ${item.action}">${ACTION_ICON[item.action]}</span>
    </span>
    <span class="c-name">${item.action === 'Add' ? '' : esc(name)}${detail}</span>
  </div>`;
}

function bindTree(tree) {
  for (const el of tree.querySelectorAll('.group'))
    el.addEventListener('click', () => {
      const action = el.dataset.group;
      state.collapsed.has(action) ? state.collapsed.delete(action) : state.collapsed.add(action);
      renderTree();
    });

  // Grup başlığındaki kutu: o gruptaki (görünen) tüm objeleri seç/kaldır.
  for (const box of tree.querySelectorAll('.gpick'))
    box.addEventListener('click', (e) => {
      e.stopPropagation();
      const on = box.checked;
      for (const c of visibleChanges())
        if (c.action === box.dataset.gpick) setPick(`${c.objectType}|${c.schema}|${c.name}`, on);
      renderTree();
    });

  for (const el of tree.querySelectorAll('.row-cat'))
    el.addEventListener('click', () => {
      const key = el.dataset.cat;
      state.collapsedCats.has(key) ? state.collapsedCats.delete(key) : state.collapsedCats.add(key);
      renderTree();
    });

  // İşaret kutuları satır seçimini tetiklemesin. Shift+tık: son tıklanan kutu ile
  // şimdiki arasındaki tüm kutuları (alt alta olanları) toplu olarak aynı duruma getirir.
  for (const box of tree.querySelectorAll('.pick:not(.gpick)'))
    box.addEventListener('click', (e) => {
      e.stopPropagation();
      const key = box.dataset.pick;
      const target = box.checked;   // tık sonrası yeni durum

      if (e.shiftKey && state.lastPick && state.lastPick !== key) {
        const boxes = [...tree.querySelectorAll('.pick:not(.gpick)')];
        const from = boxes.findIndex((b) => b.dataset.pick === state.lastPick);
        const to = boxes.findIndex((b) => b.dataset.pick === key);
        if (from !== -1 && to !== -1) {
          const [lo, hi] = from < to ? [from, to] : [to, from];
          for (let i = lo; i <= hi; i++) {
            boxes[i].checked = target;
            setPick(boxes[i].dataset.pick, target);
          }
        }
      } else {
        setPick(key, target);
      }

      state.lastPick = key;
      renderTree();   // dim/struck'ı güncelle (scroll korunur)
    });

  // ⇄ toggle: objeyi geri-alma (ters yön) için işaretle/kaldır. İndirmez — sadece işaretler.
  // ⇄ : tikli objeyi reverse yönüne al. Reverse'e giren obje forward'dan çıkar
  // (aşağıda script üretiminde ayrılır). Tik korunur — "reverse için tikli+işaretli olmalı".
  for (const btn of tree.querySelectorAll('.rev'))
    btn.addEventListener('click', (e) => {
      e.stopPropagation();
      const key = btn.dataset.rev;
      state.reversed.has(key) ? state.reversed.delete(key) : state.reversed.add(key);
      renderTree();
    });

  for (const el of tree.querySelectorAll('.row-obj, .row-child'))
    el.addEventListener('click', (e) => {
      if (e.target.dataset.toggle) {
        const key = e.target.dataset.toggle;
        state.expanded.has(key) ? state.expanded.delete(key) : state.expanded.add(key);
        renderTree();
        return;
      }
      state.selected = el.dataset.key;
      renderTree();
      loadDetail(el.dataset.schema, el.dataset.name, el.dataset.kind);
    });
}

/// İşaret kutuları "Script üret" için seçim belirler: yalnızca işaretlenen objeler
/// (ya da işaretlenen bir alt öğenin objesi) script'e girer. Hiçbir şey seçilmezse tümü.
function renderPickInfo() {
  const objectCount = selectedObjectItems().length;
  const count = state.checked.size;
  // 0 seçili durumunu gizlemek yerine açıkça söyle: boş seçim = TÜMÜ üretilir.
  $('pickedInfo').hidden = false;
  $('pickedInfo').textContent = count === 0 ? t('pickInfoAll') : t('pickInfo', { n: num(objectCount) });
}

function renderCounts() {
  const r = state.result;

  const blocking = (r?.risks ?? []).filter((x) => x.willBlock).length;
  const badge = $('cBlock');
  badge.textContent = num(blocking);
  badge.dataset.zero = blocking === 0 ? '1' : '0';

  const types = [...new Set((r?.changes ?? []).map((c) => c.objectType))].sort();
  const current = $('typeFilter').value;
  $('typeFilter').innerHTML = `<option value="">${esc(t('allTypes'))}</option>` +
    types.map((tp) => `<option value="${esc(tp)}" ${tp === current ? 'selected' : ''}>${esc(tp)}</option>`).join('');
}

function renderRisk() {
  const wrap = $('riskWrap');
  const risks = state.result?.risks ?? [];
  if (risks.length === 0) {
    wrap.innerHTML = `<p class="missing">${esc(t('noStructuralRisk'))}</p>`;
    return;
  }

  const rank = { DataLoss: 3, BlockedIfNotEmpty: 2, InPlace: 1, Safe: 0 };
  const isUnknown = (r) => r.rows === null && !r.willBlock && rank[r.risk] >= 2;
  // Önem skoru: veri kaybı > bloklanır > bilinmeyen > yerinde > güvenli.
  const sev = (r) => r.willBlock ? (r.risk === 'DataLoss' ? 4 : 3) : isUnknown(r) ? 2 : (r.risk === 'InPlace' ? 1 : 0);

  const blocking = risks.filter((r) => r.willBlock);
  const dataLoss = blocking.filter((r) => r.risk === 'DataLoss');
  const unknown = risks.filter(isUnknown);
  const clean = risks.length - blocking.length - unknown.length;

  const chip = (n, key, cls) => `<div class="risk-stat ${cls}"><b>${num(n)}</b><span>${esc(t(key))}</span></div>`;
  const summary = `<div class="risk-summary">
      ${chip(risks.length, 'riskChipTotal', 'total')}
      ${chip(blocking.length, 'riskChipBlock', 'danger')}
      ${dataLoss.length ? chip(dataLoss.length, 'riskChipLoss', 'danger') : ''}
      ${clean ? chip(clean, 'riskChipClean', 'ok') : ''}
      ${unknown.length ? chip(unknown.length, 'riskChipUnknown', 'warn') : ''}
    </div>`;

  // En riskliden en güvenliye sırala; eşitse satır sayısına göre.
  const sorted = [...risks].sort((a, b) => sev(b) - sev(a) || (b.rows ?? -1) - (a.rows ?? -1));

  const cards = sorted.map((r) => {
    let label = t('rLabelSafe'), tag = 'ok';
    if (r.willBlock && r.conditionalOnly) { label = t('rLabelCheck'); tag = 'warn'; }
    else if (r.willBlock && r.risk === 'DataLoss') { label = t('rLabelDataLoss'); tag = 'danger'; }
    else if (r.willBlock) { label = t('rLabelBlock'); tag = 'danger'; }
    else if (r.rows === 0 && rank[r.risk] >= 2) { label = t('rLabelEmpty'); tag = 'ok'; }
    else if (isUnknown(r)) { label = t('rLabelRisky'); tag = 'warn'; }
    else if (r.risk === 'InPlace') { label = t('rLabelInPlace'); tag = 'inplace'; }

    const key = `Table|${r.schema}|${r.name}`;
    const sel = state.selected === key ? ' selected' : '';
    return `<div class="risk sev-${tag}${sel}" data-key="${esc(key)}" data-schema="${esc(r.schema)}" data-name="${esc(r.name)}">
        <div class="risk-head">
          <span class="tag ${tag}">${esc(label)}</span>
          <strong>${esc(r.schema)}.${esc(r.name)}</strong>
          <span class="rows">${r.rows === null ? esc(t('rowsUnknown')) : esc(t('rowsN', { n: num(r.rows) }))}</span>
        </div>
        ${r.findings.map((f) => `<div class="finding"><span>${esc(f.column)}</span><span>${esc(f.description)}</span></div>`).join('')}
      </div>`;
  }).join('');

  wrap.innerHTML = summary + cards;

  // Karta tıkla → alt panelde o tablonun detayını aç (ağaçtaki gibi).
  for (const el of wrap.querySelectorAll('.risk'))
    el.addEventListener('click', () => {
      state.selected = el.dataset.key;
      loadDetail(el.dataset.schema, el.dataset.name, 'Table');
      renderRisk();
    });
}

function renderAll() {
  renderCounts();
  renderTree();
  renderRisk();
}

// ================= alt panel =================

async function loadDetail(schema, name, kind) {
  $('defTitle').textContent = `${kind} ${schema}.${name}`;
  $('defBody').innerHTML = `<p class="missing">${esc(t('loading'))}</p>`;

  const query = new URLSearchParams({ schema, name, kind });
  const response = await fetch(`/api/runs/${state.runId}/detail?${query}`);
  if (!response.ok) {
    $('defBody').innerHTML = `<p class="missing">${esc(t('noDefinition'))}</p>`;
    return;
  }
  state.detail = await response.json();
  renderDetail();
}

function renderDetail() {
  const detail = state.detail;
  if (!detail) { resetDiffNav(); return; }

  // Script = tam metin (modülde özgün gövde, tabloda üretilmiş CREATE).
  // Kanonik metin yalnızca "karşılaştırma neyi gördü?" sorusu için.
  const hasScript = detail.sourceScript !== null || detail.targetScript !== null;
  const useScript = hasScript && !$('showCanonical').checked;
  const left = (useScript ? detail.sourceScript : detail.sourceText) ?? null;
  const right = (useScript ? detail.targetScript : detail.targetText) ?? null;

  if (left === null && right === null) {
    $('defBody').innerHTML = `<p class="missing">${esc(t('noContent'))}</p>`;
    resetDiffNav();
    return;
  }

  const rows = alignLines(left ?? '', right ?? '');

  // Ardışık fark satırlarını tek blok say — gezinirken her satırda durmayalım.
  state.hunks = [];
  for (let i = 0; i < rows.length; i++)
    if (rows[i].type !== 'same' && (i === 0 || rows[i - 1].type === 'same')) state.hunks.push(i);
  const hunkStarts = new Set(state.hunks);
  state.hunkIndex = -1;

  // Renklendirme metnin tamamı üzerinden yapılır (blok yorum durumu satırlar arası
  // taşınsın), sonra satır numarasıyla eşleştirilir. Kanonik metin SQL değil, boyanmaz.
  const leftHtml = useScript && left !== null ? highlightLines(left.split('\n')) : null;
  const rightHtml = useScript && right !== null ? highlightLines(right.split('\n')) : null;

  const pane = (side, highlighted) => rows.map((row, i) => {
    const mark = hunkStarts.has(i) ? ' hunk' : '';
    if (row[side] === null)
      return `<div class="dline pad${mark}" data-row="${i}"><span class="ln"></span><span></span></div>`;

    const cls = row.type === 'same' ? '' : (side === 'left' ? 'del' : 'add');
    const lineNo = row[side + 'No'];
    const code = (highlighted ? highlighted[lineNo - 1] : esc(row[side])) || '&nbsp;';
    return `<div class="dline ${cls}${mark}" data-row="${i}">` +
           `<span class="ln">${lineNo}</span><span class="code">${code}</span></div>`;
  }).join('');

  $('defBody').innerHTML = `<div class="diff-grid">
    <div class="diff-pane"><div class="pane-head">${esc(t('source'))}${left === null ? esc(t('missingSuffix')) : ''}</div>
      ${left === null ? `<p class="missing">${esc(t('notOnThisSide'))}</p>` : pane('left', leftHtml)}</div>
    <div class="diff-pane"><div class="pane-head">${esc(t('target'))}${right === null ? esc(t('missingSuffix')) : ''}</div>
      ${right === null ? `<p class="missing">${esc(t('notOnThisSide'))}</p>` : pane('right', rightHtml)}</div>
  </div>`;

  state.diffRowCount = rows.length;
  state.wordTerm = '';     // yeni obje → kelime vurgusunu sıfırla
  buildMinimap(rows);      // kırmızı/yeşil fark işaretleri
  updateDiffNav();
  if (state.hunks.length) gotoHunk(0);
}

// Sağ minimap: her fark satırı için kırmızı (silme) / yeşil (ekleme) işaret.
// data-diff = temel (fark) işaretleri; kelime eşleşmeleri bunun üstüne eklenir.
function buildMinimap(rows) {
  const mm = $('diffMinimap');
  if (!mm) return;
  const n = rows.length || 1;
  let html = '';
  rows.forEach((row, i) => {
    if (row.type === 'same') return;
    const top = (i / n) * 100;
    if (row.left !== null && row.left !== undefined)
      html += `<div class="mm del" style="top:${top}%" data-row="${i}"></div>`;
    if (row.right !== null && row.right !== undefined)
      html += `<div class="mm add" style="top:${top}%" data-row="${i}"></div>`;
  });
  mm.dataset.diff = html;
  mm.innerHTML = html;
}

function clearMinimap() {
  const mm = $('diffMinimap');
  if (mm) { mm.dataset.diff = ''; mm.innerHTML = ''; }
}

// Minimap'e kelime eşleşmelerini (sarı) fark işaretlerinin ÜSTÜNE ekle.
function setWordMarkers(rowIndices) {
  const mm = $('diffMinimap');
  if (!mm) return;
  const n = state.diffRowCount || 1;
  let extra = '';
  for (const i of rowIndices) extra += `<div class="mm word" style="top:${(i / n) * 100}%" data-row="${i}"></div>`;
  mm.innerHTML = (mm.dataset.diff || '') + extra;
}

// Önceki sarı vurguları kaldır (mark'ları düz metne çevir).
function clearWordHighlights() {
  const body = $('defBody');
  if (!body) return;
  for (const m of body.querySelectorAll('mark.wordhit')) m.replaceWith(document.createTextNode(m.textContent));
  body.normalize();
}

// Seçilen kelimenin (term) alt paneldeki TÜM geçtiği yerleri sarı işaretle;
// eşleşen satırları minimap'te de sarı göster. Söz dizimi renklendirmesini bozmadan,
// yalnızca metin düğümleri sarılır.
function highlightWord(term) {
  clearWordHighlights();
  const body = $('defBody');
  if (!body) return;
  if (!term || term.length < 2) { setWordMarkers([]); return; }

  const lower = term.toLowerCase();
  const rowsHit = new Set();
  const walker = document.createTreeWalker(body, NodeFilter.SHOW_TEXT, {
    acceptNode: (node) => {
      const p = node.parentElement;
      if (!node.nodeValue || !p) return NodeFilter.FILTER_REJECT;
      if (p.closest('.ln')) return NodeFilter.FILTER_REJECT;              // satır numaralarını atla
      return node.nodeValue.toLowerCase().includes(lower) ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_REJECT;
    },
  });
  const targets = [];
  while (walker.nextNode()) targets.push(walker.currentNode);

  for (const node of targets) {
    const text = node.nodeValue;
    const low = text.toLowerCase();
    const frag = document.createDocumentFragment();
    let idx = 0, pos;
    while ((pos = low.indexOf(lower, idx)) !== -1) {
      if (pos > idx) frag.appendChild(document.createTextNode(text.slice(idx, pos)));
      const mark = document.createElement('mark');
      mark.className = 'wordhit';
      mark.textContent = text.slice(pos, pos + term.length);
      frag.appendChild(mark);
      idx = pos + term.length;
      const dline = node.parentElement.closest('.dline');
      if (dline) rowsHit.add(+dline.dataset.row);
    }
    if (idx < text.length) frag.appendChild(document.createTextNode(text.slice(idx)));
    node.replaceWith(frag);
  }
  setWordMarkers([...rowsHit]);
}

function resetDiffNav() {
  state.hunks = [];
  state.hunkIndex = -1;
  state.diffRowCount = 0;
  state.wordTerm = '';
  clearMinimap();
  updateDiffNav();
}

function updateDiffNav() {
  const total = state.hunks.length;
  $('diffCount').textContent = total === 0 ? t('noDiff') : t('diffOfTotal', { i: state.hunkIndex + 1, n: total });
  $('prevDiff').disabled = total === 0;
  $('nextDiff').disabled = total === 0;
}

/// Fark bloğunu görünümün ortasına getirir — öncesi ve sonrası birlikte görünsün.
function gotoHunk(index) {
  const total = state.hunks.length;
  if (total === 0) return;

  state.hunkIndex = ((index % total) + total) % total;
  const row = state.hunks[state.hunkIndex];
  document.querySelector(`#defBody .dline[data-row="${row}"]`)?.scrollIntoView({ block: 'center' });
  updateDiffNav();
}

/**
 * Satır bazlı LCS ile iki metni hizalar. Çok büyük metinlerde O(n*m) tablo
 * pahalıya gelir; o durumda hizalamadan yan yana gösterilir.
 */
function alignLines(a, b) {
  const A = a.split('\n'), B = b.split('\n');
  const LIMIT = 2000;

  // Eşleştirme boşluk farklarını yok sayar: tablo CREATE'inde kolon hizalama boşluğu
  // (en uzun ada göre PadRight) bir kolon eklenince tüm satırlarda kayar; bu, yalnızca
  // gerçekten değişen satırın işaretlenmesini sağlar. Görüntülenen metin özgün hâlidir.
  // Ayrıca SONDAKİ VİRGÜL yok sayılır: son kolona kolon eklenince önceki satır virgül
  // kazanır — tanım aynı olduğu hâlde fark görünürdü. Virgül liste ayıracıdır, anlam taşımaz.
  const norm = (s) => (s ?? '').replace(/\s+/g, ' ').trim().replace(/,$/, '');
  const An = A.map(norm), Bn = B.map(norm);

  if (A.length > LIMIT || B.length > LIMIT) {
    const rows = [];
    for (let i = 0; i < Math.max(A.length, B.length); i++)
      rows.push({
        type: An[i] === Bn[i] ? 'same' : 'diff',
        left: A[i] ?? null, right: B[i] ?? null,
        leftNo: i < A.length ? i + 1 : '', rightNo: i < B.length ? i + 1 : '',
      });
    return rows;
  }

  const table = Array.from({ length: A.length + 1 }, () => new Uint32Array(B.length + 1));
  for (let i = A.length - 1; i >= 0; i--)
    for (let j = B.length - 1; j >= 0; j--)
      table[i][j] = An[i] === Bn[j] ? table[i + 1][j + 1] + 1 : Math.max(table[i + 1][j], table[i][j + 1]);

  const ops = [];
  let i = 0, j = 0;
  while (i < A.length && j < B.length) {
    if (An[i] === Bn[j]) { ops.push({ t: 'same', a: A[i], b: B[j] }); i++; j++; }
    else if (table[i + 1][j] >= table[i][j + 1]) { ops.push({ t: 'del', a: A[i] }); i++; }
    else { ops.push({ t: 'add', b: B[j] }); j++; }
  }
  while (i < A.length) ops.push({ t: 'del', a: A[i++] });
  while (j < B.length) ops.push({ t: 'add', b: B[j++] });

  // Silme ve ekleme bloklarını yan yana eşle — okunması kolay olsun.
  const rows = [];
  let leftNo = 0, rightNo = 0, k = 0;
  while (k < ops.length) {
    if (ops[k].t === 'same') {
      rows.push({ type: 'same', left: ops[k].a, right: ops[k].b, leftNo: ++leftNo, rightNo: ++rightNo });
      k++;
      continue;
    }
    const dels = [], adds = [];
    while (k < ops.length && ops[k].t !== 'same') {
      (ops[k].t === 'del' ? dels : adds).push(ops[k].t === 'del' ? ops[k].a : ops[k].b);
      k++;
    }
    for (let n = 0; n < Math.max(dels.length, adds.length); n++)
      rows.push({
        type: 'diff',
        left: n < dels.length ? dels[n] : null,
        right: n < adds.length ? adds[n] : null,
        leftNo: n < dels.length ? ++leftNo : '',
        rightNo: n < adds.length ? ++rightNo : '',
      });
  }
  return rows;
}

// ================= olaylar =================

$('sourceBtn').addEventListener('click', () => openConnect('source'));
$('targetBtn').addEventListener('click', () => openConnect('target'));
$('swapBtn').addEventListener('click', () => {
  [state.source, state.target] = [state.target, state.source];
  renderEndpoints();
});

$('connClose').addEventListener('click', closeConnect);
$('connCancel').addEventListener('click', closeConnect);
$('connScrim').addEventListener('click', closeConnect);
$('connOk').addEventListener('click', confirmConnection);
$('connTest').addEventListener('click', testConnection);
$('cAuth').addEventListener('change', syncAuthRows);

// "Listele" butonu yerine: bağlantı bilgileri hazır olunca veritabanlarını otomatik getir.
// Aynı bağlantı için tekrar tekrar çekmemek için imza (signature) ile guard'lanır.
let dbLoadedSig = null;   // en son başarıyla listelenen bağlantının imzası
let dbLoading = false;    // devam eden bir listeleme var mı
function connSig() {
  const c = readDialogConnection();
  return [c.server, c.authentication, c.userName ?? '', c.password ?? '', c.encrypt, c.trustServerCertificate].join('|');
}
function maybeAutoLoad({ force = false } = {}) {
  const c = readDialogConnection();
  if (!c.server) return;                                        // sunucu şart
  if (c.authentication === 'SqlLogin' && !c.userName) return;   // SQL modunda kullanıcı adı şart
  const sig = connSig();
  if (dbLoading) return;                                        // zaten yükleniyor
  if (!force && sig === dbLoadedSig) return;                    // bu bağlantı için zaten listelendi
  dbLoading = true;
  loadDatabases().then((ok) => { if (ok) dbLoadedSig = sig; }).finally(() => { dbLoading = false; });
}
// Sunucu / kimlik alanları değişince önden yükle (kutuya varmadan hazır olsun)
for (const id of ['cServer', 'cAuth', 'cUser', 'cPass'])
  $(id).addEventListener('change', () => maybeAutoLoad());
// Veritabanı kutusuna tıklayınca/odaklanınca da getir — butona gerek kalmadan.
$('cDatabase').addEventListener('mousedown', () => maybeAutoLoad());
$('cDatabase').addEventListener('focus', () => maybeAutoLoad());

$('optionsBtn').addEventListener('click', () => {
  renderOptions();
  $('optScrim').hidden = false;
  $('optDialog').hidden = false;
});
const closeOptions = () => { $('optScrim').hidden = true; $('optDialog').hidden = true; };
$('optClose').addEventListener('click', closeOptions);
$('optOk').addEventListener('click', closeOptions);
$('optScrim').addEventListener('click', closeOptions);
$('optReset').addEventListener('click', () => { state.options = { ...DEFAULT_OPTIONS }; renderOptions(); });

$('compareBtn').addEventListener('click', compare);
$('showCanonical').addEventListener('change', renderDetail);
$('prevDiff').addEventListener('click', () => gotoHunk(state.hunkIndex - 1));
$('nextDiff').addEventListener('click', () => gotoHunk(state.hunkIndex + 1));

// Alt panelde bir kelime seçince: tüm geçtiği yerleri sarı işaretle + minimap'e yansıt.
// Vurgu kalıcıdır — yeni bir kelime seçilene ya da obje değişene kadar durur. Boşluk içeren
// (çok kelimeli) seçim vurgulanmaz. Aynı kelimeyi tekrar çift-tıklayınca temizlenir (toggle).
$('defBody').addEventListener('mouseup', () => {
  const sel = window.getSelection();
  const term = sel ? sel.toString().trim() : '';
  if (!term || /\s/.test(term) || !sel.anchorNode || !$('defBody').contains(sel.anchorNode)) return;
  if (term.length < 2) return;
  highlightWord(term === state.wordTerm ? '' : term);
  state.wordTerm = term === state.wordTerm ? '' : term;
});

// Minimap'e tıkla → ilgili satıra git (işaret yoksa orantısal konuma kaydır).
$('diffMinimap').addEventListener('click', (e) => {
  const diff = $('defBody');
  const marker = e.target.closest('.mm');
  if (marker) {
    document.querySelector(`#defBody .dline[data-row="${marker.dataset.row}"]`)?.scrollIntoView({ block: 'center' });
    return;
  }
  const rect = e.currentTarget.getBoundingClientRect();
  const ratio = (e.clientY - rect.top) / rect.height;
  diff.scrollTop = ratio * (diff.scrollHeight - diff.clientHeight);
});

// Karşılaştırma biter bitmez tüm değişiklikler seçili gelir — kullanıcı istemediklerini
// kaldırır (dahil et değil, hariç tut mantığı). Hem obje satırı hem alt öğeleri işaretlenir
// ki obje açıldığında içindekiler de seçili görünsün.
function selectAllChanges() {
  state.checked.clear();
  for (const c of state.result?.changes ?? []) {
    const objKey = `${c.objectType}|${c.schema}|${c.name}`;
    state.checked.add(objKey);
    for (const child of c.children ?? [])
      state.checked.add(`${objKey}›${child.category}›${child.name}`);
  }
}

// İşaretlenen her anahtar 'Type|schema|name' ya da onun altında 'Type|schema|name›kategori›öğe'
// biçimindedir. Alt öğe işaretlense de obje kimliğini çıkarıp objeyi script'e alırız — böylece
// bir kolonu işaretlemek, o tablonun değişikliğini script'e sokar.
function selectedObjectItems() {
  const seen = new Map();
  for (const key of state.checked) {
    if (typeof key !== 'string' || key.length === 0) continue;   // güvenlik: bozuk anahtar atla
    const objId = key.split('›')[0];
    if (seen.has(objId)) continue;
    const parts = objId.split('|');
    seen.set(objId, { objectType: parts[0], schema: parts[1] ?? '', name: parts.slice(2).join('|') });
  }
  return [...seen.values()];
}

// Script üret: iki AYRI seçimle iki AYRI script.
//   forward = source → target — ⇄ İŞARETSİZ tikli objeler (dağıtım).
//   reverse = target → source — YALNIZ ⇄ işaretli (ve tikli) objeler (geri alma).
// ⇄ işaretli obje forward'dan çıkar. Reverse seçim boşsa o sekme boş kalır.
async function generateScripts(forwardSel, reverseSel) {
  if (!state.runId) return;
  $('scriptBtn').disabled = true;
  setStatus(t('generatingScript'), 'busy');

  const call = (selection, reverse) => fetch(`/api/runs/${state.runId}/script`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      scope: 'all',
      dataLoss: !state.options.blockDataLoss,
      dropNotInSource: state.options.dropNotInSource,
      scriptValidateNewConstraints: state.options.scriptValidateConstraints,
      selection, reverse,
    }),
  }).then((r) => { if (!r.ok) throw new Error(t('scriptFailed')); return r.json(); });

  try {
    const [forward, reverse] = await Promise.all([call(forwardSel, false), call(reverseSel, true)]);
    openScriptEditor(forwardSel.length, reverseSel.length, forward, reverse);
    setStatus(t('scriptReady'));
  } catch (error) {
    setStatus(error.message, 'error');
  } finally {
    $('scriptBtn').disabled = false;
  }
}

// Tikli objeleri ⇄ işaretine göre ikiye ayır: forward (işaretsiz) / reverse (işaretli).
$('scriptBtn').addEventListener('click', () => {
  const all = selectedObjectItems();
  const keyOf = (o) => `${o.objectType}|${o.schema}|${o.name}`;
  const forwardSel = all.filter((o) => !state.reversed.has(keyOf(o)));
  const reverseSel = all.filter((o) => state.reversed.has(keyOf(o)));
  generateScripts(forwardSel, reverseSel);
});

// Bir yönün özet satırı; içerik yoksa "değişiklik yok".
function scriptInfoLine(selLen, data) {
  if (!data.sql || !data.sql.trim()) return t('emptyScriptInfo');
  const parts = [selLen > 0
    ? t('scriptIncluded', { n: num(data.included), sel: num(selLen) })
    : t('scriptIncludedAll', { n: num(data.included) })];
  if (!state.options.blockDataLoss) parts.push(t('dataLossIncluded'));
  if ((data.dataLossActions || []).length) parts.push(t('stillGated', { n: num(data.dataLossActions.length) }));
  if (data.outOfScope > 0) parts.push(t('outOfScopeN', { n: num(data.outOfScope) }));
  if ((data.skipped || []).length) parts.push(t('skippedN', { n: num(data.skipped.length) }));
  return parts.join(' · ');
}

// ---- üretilen script editörü (renkli + düzenlenebilir + indir + yerel oto-tamamlama) ----
let scriptFileName = 'script.sql';
let gutterLines = -1;

function highlightEditor() {
  const ta = $('codeInput'), hl = $('codeHl');
  const arr = ta.value.split('\n');
  hl.innerHTML = highlightLines(arr).join('\n');
  hl.scrollTop = ta.scrollTop; hl.scrollLeft = ta.scrollLeft;
  // Satır numaraları — yalnızca satır sayısı değişince yeniden kur (büyük script'te hızlı).
  if (arr.length !== gutterLines) {
    gutterLines = arr.length;
    let nums = '';
    for (let i = 1; i <= arr.length; i++) nums += i + '\n';
    $('codeGutter').textContent = nums;
  }
  $('codeGutter').scrollTop = ta.scrollTop;
}

function openScriptEditor(fwdLen, revLen, forward, reverse) {
  const revEmpty = !(reverse.sql && reverse.sql.trim());
  state.scriptDirs = {
    forward: { sql: forward.sql || '', fileName: forward.fileName || 'script.sql', info: scriptInfoLine(fwdLen, forward) },
    reverse: { sql: reverse.sql || '', fileName: reverse.fileName || 'reverse.sql', info: scriptInfoLine(revLen, reverse), empty: revEmpty },
  };
  // Reverse sekmesi boşsa etikette belli et.
  const revTab = document.querySelector('#scriptDialog .stab[data-dir="reverse"]');
  if (revTab) revTab.classList.toggle('empty', revEmpty);

  scriptVocab = buildVocab();
  $('scriptScrim').hidden = false;
  $('scriptDialog').hidden = false;
  showScriptDir('forward');
}

// Aktif yönü editöre yükle. İndir/kopyala codeInput + scriptFileName üzerinden çalışır.
function showScriptDir(dir) {
  const d = state.scriptDirs?.[dir];
  if (!d) return;
  state.scriptActiveDir = dir;
  for (const b of document.querySelectorAll('#scriptDialog .stab')) b.classList.toggle('active', b.dataset.dir === dir);
  scriptFileName = d.fileName;
  $('codeInput').value = d.sql;
  $('scriptInfo').textContent = d.info || '';
  gutterLines = -1;
  $('codeInput').scrollTop = 0;
  highlightEditor();
}

function closeScriptEditor() { closeAutocomplete(); $('scriptScrim').hidden = true; $('scriptDialog').hidden = true; }

// ===== yerel (AI'sız) şema-farkında oto-tamamlama =====
// Sözlük: T-SQL keyword/tip/fonksiyon + karşılaştırmadaki şema/tablo/kolon/obje adları
// + script içindeki [köşeli] adlar. Hiçbir veri makineden çıkmaz.
let scriptVocab = [];
let ac = { open: false, items: [], index: 0, start: 0, bracket: false };

function buildVocab() {
  const map = new Map();
  const add = (w, kind) => { if (w && !map.has(w)) map.set(w, { word: w, kind }); };
  for (const k of SQL_KEYWORDS) add(k, 'kw');
  for (const k of SQL_TYPES) add(k, 'type');
  for (const k of SQL_FUNCTIONS) add(k, 'fn');
  for (const c of state.result?.changes ?? []) {
    if (c.schema) add(c.schema, 'schema');
    if (c.name) add(c.name, c.objectType === 'Table' ? 'table' : 'obj');
    for (const ch of c.children ?? []) add(ch.name, ch.category === 'Columns' ? 'col' : 'id');
  }
  for (const m of ($('codeInput').value || '').matchAll(/\[([^\]\r\n]+)\]/g)) add(m[1], 'id');
  return [...map.values()];
}

// İmleçten geriye doğru yazılmakta olan kelimeyi ve köşeli-parantez bağlamını bulur.
function currentToken() {
  const ta = $('codeInput'), pos = ta.selectionStart, text = ta.value;
  let i = pos;
  while (i > 0 && /[A-Za-z0-9_@#$]/.test(text[i - 1])) i--;
  return { token: text.slice(i, pos), start: i, bracket: i > 0 && text[i - 1] === '[' };
}

function updateAutocomplete() {
  const ta = $('codeInput');
  if (ta.selectionStart !== ta.selectionEnd) return closeAutocomplete();
  const { token, start, bracket } = currentToken();
  if (!bracket && token.length < 2) return closeAutocomplete();   // gürültüyü azalt
  const q = token.toLowerCase();
  const items = scriptVocab
    .filter((v) => v.word.toLowerCase().startsWith(q) && v.word.toLowerCase() !== q)
    .sort((a, b) => a.word.localeCompare(b.word))
    .slice(0, 12);
  if (items.length === 0) return closeAutocomplete();
  ac = { open: true, items, index: 0, start, bracket };
  renderAcPopup();
}

function renderAcPopup() {
  const pop = $('acPopup');
  pop.innerHTML = ac.items.map((v, i) =>
    `<div class="ac-item${i === ac.index ? ' active' : ''}" data-i="${i}">${esc(v.word)}<span class="ac-kind">${v.kind}</span></div>`).join('');
  const { x, y } = caretCoords($('codeInput'));
  pop.style.left = Math.min(x, window.innerWidth - 350) + 'px';
  pop.style.top = (y + 18) + 'px';
  pop.hidden = false;
  for (const el of pop.querySelectorAll('.ac-item'))
    el.addEventListener('mousedown', (e) => { e.preventDefault(); ac.index = +el.dataset.i; acceptAutocomplete(); });
}

function closeAutocomplete() { ac.open = false; $('acPopup').hidden = true; }

function acceptAutocomplete() {
  if (!ac.open) return;
  const ta = $('codeInput'), word = ac.items[ac.index].word, pos = ta.selectionStart;
  const after = ta.value.slice(pos);
  const insert = (ac.bracket && after[0] !== ']') ? word + ']' : word;
  ta.value = ta.value.slice(0, ac.start) + insert + after;
  ta.selectionStart = ta.selectionEnd = ac.start + word.length + (insert.length - word.length);
  closeAutocomplete();
  highlightEditor();
}

// İmlecin ekran koordinatı: textarea'yı birebir taklit eden gizli div + işaretçi span.
function caretCoords(ta) {
  const div = document.createElement('div'), style = getComputedStyle(ta);
  for (const p of ['boxSizing', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft',
    'borderWidth', 'fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'letterSpacing', 'tabSize'])
    div.style[p] = style[p];
  Object.assign(div.style, { position: 'absolute', visibility: 'hidden', whiteSpace: 'pre', top: '0', left: '0' });
  div.textContent = ta.value.slice(0, ta.selectionStart);
  const span = document.createElement('span'); span.textContent = '​'; div.appendChild(span);
  document.body.appendChild(div);
  const rect = ta.getBoundingClientRect();
  const x = rect.left + span.offsetLeft - ta.scrollLeft;
  const y = rect.top + span.offsetTop - ta.scrollTop;
  document.body.removeChild(div);
  return { x, y };
}

$('codeInput').addEventListener('input', () => { highlightEditor(); updateAutocomplete(); });
$('codeInput').addEventListener('scroll', () => {
  $('codeHl').scrollTop = $('codeInput').scrollTop;
  $('codeHl').scrollLeft = $('codeInput').scrollLeft;
  $('codeGutter').scrollTop = $('codeInput').scrollTop;
  closeAutocomplete();
});
$('codeInput').addEventListener('blur', () => setTimeout(closeAutocomplete, 120));
$('codeInput').addEventListener('click', closeAutocomplete);
$('codeInput').addEventListener('keydown', (e) => {
  // Oto-tamamlama açıkken oklar/Enter/Tab/Esc listeyi yönetir.
  if (ac.open) {
    if (e.key === 'ArrowDown') { e.preventDefault(); ac.index = (ac.index + 1) % ac.items.length; renderAcPopup(); return; }
    if (e.key === 'ArrowUp') { e.preventDefault(); ac.index = (ac.index - 1 + ac.items.length) % ac.items.length; renderAcPopup(); return; }
    if (e.key === 'Enter' || e.key === 'Tab') { e.preventDefault(); acceptAutocomplete(); return; }
    if (e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); closeAutocomplete(); return; }
  }
  // Ctrl+Space: öneriyi elle aç.
  if (e.key === ' ' && e.ctrlKey) { e.preventDefault(); updateAutocomplete(); return; }
  // Tab tuşu odağı kaydırmasın; SQL'e sekme eklesin.
  if (e.key === 'Tab') {
    e.preventDefault();
    const ta = e.target, s = ta.selectionStart, en = ta.selectionEnd;
    ta.value = ta.value.slice(0, s) + '\t' + ta.value.slice(en);
    ta.selectionStart = ta.selectionEnd = s + 1;
    highlightEditor();
  }
});
$('scriptClose').addEventListener('click', closeScriptEditor);
$('scriptScrim').addEventListener('click', closeScriptEditor);
// Yön sekmeleri: source→target / target→source. Aktif sekmenin script'i editörde.
for (const tab of document.querySelectorAll('#scriptDialog .stab'))
  tab.addEventListener('click', () => showScriptDir(tab.dataset.dir));
$('scriptDownloadBtn').addEventListener('click', () => {
  // UTF-8 BOM: sqlcmd/SSMS Türkçe karakterleri doğru okusun.
  const url = URL.createObjectURL(new Blob(['﻿' + $('codeInput').value], { type: 'application/sql' }));
  const link = document.createElement('a');
  link.href = url; link.download = scriptFileName; link.click();
  URL.revokeObjectURL(url);
  setStatus(t('sqlDownloaded', { file: scriptFileName }));
});
$('scriptCopyBtn').addEventListener('click', async () => {
  await navigator.clipboard.writeText($('codeInput').value);
  setStatus(t('scriptCopied'));
});

$('typeFilter').addEventListener('change', renderTree);
$('search').addEventListener('input', renderTree);

for (const tab of document.querySelectorAll('.vtab')) {
  tab.addEventListener('click', () => {
    for (const other of document.querySelectorAll('.vtab')) other.classList.toggle('active', other === tab);
    $('treeWrap').hidden = tab.dataset.view !== 'tree';
    $('riskWrap').hidden = tab.dataset.view !== 'risk';
    $('gridhead').hidden = tab.dataset.view !== 'tree';
  });
}

document.addEventListener('keydown', (e) => {
  if (e.key !== 'Escape') return;
  if (!$('connDialog').hidden) closeConnect();
  if (!$('optDialog').hidden) closeOptions();
  if (!$('scriptDialog').hidden) closeScriptEditor();
});

// Üst/alt panel arasındaki sürüklenebilir ayırıcı.
(() => {
  const splitter = $('splitter');
  const upper = document.querySelector('.upper');
  let dragging = false;

  splitter.addEventListener('mousedown', (e) => { dragging = true; e.preventDefault(); });
  window.addEventListener('mouseup', () => { dragging = false; });
  window.addEventListener('mousemove', (e) => {
    if (!dragging) return;
    const top = splitter.parentElement.getBoundingClientRect().top;
    const height = splitter.parentElement.clientHeight;
    const ratio = Math.min(0.85, Math.max(0.15, (e.clientY - top) / height));
    upper.style.flex = `1 1 ${ratio * 100}%`;
    document.querySelector('.lower').style.flex = `1 1 ${(1 - ratio) * 100}%`;
  });
})();

// Yeni karşılaştırma: aynı adresi yeni sekmede açar. Her sekme bağımsız oturumdur.
$('newCompareBtn').addEventListener('click', () => window.open(location.href, '_blank'));

// ---- tema / dil geçişi ----
$('themeBtn').addEventListener('click', () => {
  THEME = THEME === 'dark' ? 'light' : 'dark';
  localStorage.setItem('theme', THEME);
  applyTheme();
});
$('langBtn').addEventListener('click', () => {
  LANG = LANG === 'en' ? 'tr' : 'en';
  localStorage.setItem('lang', LANG);
  applyI18n();
  renderEndpoints();
  if (state.result) renderAll();
  if (state.detail) renderDetail();
  if (!$('optDialog').hidden) renderOptions();   // ayarlar açıksa etiket/açıklamaları da çevir
});

applyTheme();
applyI18n();
renderEndpoints();
renderAll();
