const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { migrate, renameText } = require('./migrate-element-party-names.cjs');

test('all element references preserve roles and order', () => {
  assert.equal(renameText('钟纳娜万,钟班香万,钟心那万,钟芙琴万,钟纳久万,钟心纳久,钟迪绫万,钟心娜万'), '矿物,火,水,风,雷,草,冰,岩');
  assert.equal(renameText('00-钟心那万.txt / 那维莱特 / 钟离'), '00-水.txt / 那维莱特 / 钟离');
});

test('key-only migration is backed up, idempotent, and leaves names, records and task order unchanged', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bettergi-key-test-'));
  const groupPath = path.join(root, 'User/ScriptGroup/养成一条龙-102550550.json');
  const script = path.join(root, 'User/JsScript/AutoPlan');
  fs.mkdirSync(path.dirname(groupPath), { recursive: true });
  fs.mkdirSync(script, { recursive: true });
  const group = { name: '养成一条龙-102550550', config: { partyName: '钟心那万', taskOrder: ['b', 'a'] }, projects: [{ folderName: 'AutoPlan', jsScriptSettingsObject: { key: 'stale', other: 42 } }, { folderName: 'Other', jsScriptSettingsObject: { key: 'unrelated' } }] };
  fs.writeFileSync(groupPath, JSON.stringify(group));
  fs.writeFileSync(path.join(script, 'manifest.json'), '{"key":"current"}');
  fs.writeFileSync(path.join(script, 'settings.json'), '[{"name":"key","default":"stale"}]');
  const dry = migrate(root, { keysOnly: true });
  assert.equal(dry.updates.length, 2);
  assert.equal(JSON.parse(fs.readFileSync(groupPath)).projects[0].jsScriptSettingsObject.key, 'stale');
  const result = migrate(root, { keysOnly: true, apply: true });
  assert.ok(fs.existsSync(path.join(result.backup, 'User/ScriptGroup/养成一条龙-102550550.json')));
  group.projects[0].jsScriptSettingsObject.key = 'current';
  assert.deepEqual(JSON.parse(fs.readFileSync(groupPath)), group);
  assert.equal(migrate(root, { keysOnly: true, apply: true }).updates.length, 0);
});
