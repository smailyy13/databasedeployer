// Run with: node tools/check-i18n.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '..');
const read = (name) => fs.readFileSync(path.join(root, name), 'utf8');
const js = read('src/SchemaDiff.Web/wwwroot/app.js');
const html = read('src/SchemaDiff.Web/wwwroot/index.html');
const localization = read('src/SchemaDiff.Web/wwwroot/localization.js');
const context = vm.createContext({ localStorage: { getItem: () => null } });
vm.runInContext(localization + '\n' + js.slice(0, js.indexOf('// ================= T-SQL')) + `
  globalThis.audit = { I18N, UI_LABELS_TR, RISK_MESSAGES, riskText, riskColumnLabel, changeDetail,
    uiLabel, autocompleteLabel, localizedUiText, t, language: (language) => LANG = language };
`, context);
const a = context.audit;
assert.deepEqual(Object.keys(a.I18N.en).sort(), Object.keys(a.I18N.tr).sort(), 'EN/TR key parity');
const vars = (s) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
for (const key of Object.keys(a.I18N.en)) {
  assert.ok(a.I18N.en[key] && a.I18N.tr[key], `Empty translation: ${key}`);
  assert.deepEqual(vars(a.I18N.en[key]), vars(a.I18N.tr[key]), `Placeholder mismatch: ${key}`);
}
const refs = [
  ...[...js.matchAll(/\bt\(['"]([^'"]+)['"]/g)].map((m) => m[1]).filter((key) => !key.endsWith('_')),
  ...[...html.matchAll(/data-i18n(?:-title|-ph|-aria)?="([^"]+)"/g)].map((m) => m[1]),
];
vm.runInContext(js.slice(js.indexOf('const OPTION_GROUPS ='), js.indexOf("const OPTIONS_KEY =")) + '\nglobalThis.groups = OPTION_GROUPS;', context);
for (const group of context.groups) refs.push(group.group, ...group.keys.flatMap((key) => ['opt_' + key, 'optn_' + key]));
for (const key of refs) assert.ok(key in a.I18N.en && key in a.I18N.tr, `Missing translation: ${key}`);
const objectLabels = read('src/SchemaDiff.Core/Model/ObjectKindLabels.cs');
for (const match of objectLabels.matchAll(/ObjectKind\.\w+ => "([^"]+)"/g))
  assert.ok(match[1] in a.UI_LABELS_TR, `Untranslated object kind: ${match[1]}`);
const catalog = read('src/SchemaDiff.Core/Analysis/ChangeCatalog.cs');
for (const match of catalog.matchAll(/new ChildChange\([^\n]*?"([^"]+)", "([^"]+)"/g))
  for (const label of [match[1], match[2]]) assert.ok(label in a.UI_LABELS_TR, `Untranslated child label: ${label}`);

// All risk templates must work with dynamic SQL/identifiers and retain their values.
for (const [tr, en] of a.RISK_MESSAGES) {
  const sample = (s) => s.replace(/\{(\w+)\}/g, (_, name) => `[${name}: Türkçe_ekleniyor('value')]`);
  assert.equal(a.riskText(sample(tr), 'en'), sample(en), `Risk translation: ${tr}`);
  assert.equal(a.riskText(sample(tr), 'tr'), sample(tr));
  assert.deepEqual(vars(tr), vars(en));
}
const unchanged = "DEFAULT N'IDENTITY ekleniyor, veri korunur'";
assert.equal(a.changeDetail({ itemType: 'Default Constraint', detail: unchanged }, 'en'), unchanged);
assert.equal(a.changeDetail({ itemType: 'Extended Property', detail: 'Gövde metni değişti' }, 'en'), 'Gövde metni değişti');
assert.equal(a.changeDetail({ itemType: 'Column', detail: 'IDENTITY ekleniyor, SPARSE kaldırılıyor' }, 'en'), 'Adding IDENTITY, Removing SPARSE');
assert.equal(a.uiLabel('Foreign Keys', 'tr'), 'Yabancı Anahtarlar');
assert.equal(a.uiLabel('Foreign Keys', 'en'), 'Foreign Keys');
assert.equal(a.riskColumnLabel('(performans)', 'en'), '(performance)');
assert.equal(a.autocompleteLabel('kw', 'tr'), 'Anahtar kelime');
a.language('en');
assert.equal(a.localizedUiText('Kaynak ve hedef sunucu zorunlu.'), 'Source and target servers are required.');
assert.equal(a.localizedUiText('Bu koşum için bağlantı bilgisi yok (yeniden karşılaştırın).'), 'No connection information for this run. Compare again.');
a.language('tr');
assert.equal(a.localizedUiText('Script copied to clipboard'), 'Script panoya kopyalandı');
assert.equal(a.t('execRowsAffected', { n: 17 }), '(17 satır etkilendi)');
const exec = read('src/SchemaDiff.Web/ScriptExecutor.cs');
for (const match of exec.matchAll(/"(exec\w+)"/g)) assert.ok(match[1] in a.I18N.en && match[1] in a.I18N.tr, `Missing execution key: ${match[1]}`);
assert.ok(read('README.md').includes('(docs/SchemaDiff-Kullanim-Kilavuzu.pdf)'));
assert.ok(fs.existsSync(path.join(root, 'docs/SchemaDiff-Kullanim-Kilavuzu.pdf')));
console.log(`EN/TR audit passed: ${Object.keys(a.I18N.en).length} paired keys, ${a.RISK_MESSAGES.length} risk templates, labels, placeholders, execution messages and README link.`);
