const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const { updateGroup, guardSetupScript, presets, assertRefreshRuntime } = require('./sync-element-party-presets.cjs');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

test('refresh scripts cannot be installed without a compatible published runtime', () => {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'bettergi-refresh-gate-'));
  assert.throws(() => assertRefreshRuntime(root, root), /Publish and install/);
});

test('nine presets update water, make both utility parties double-anemo and preserve unrelated settings', () => {
  const group = { config: { taskOrder: ['b', 'a'] }, projects: [{ name: '配置水', folderName: 'AutoSwitchRoles', jsScriptSettingsObject: { switchPartyName: '水', option: 'unchanged', position1: '钟离', position2: '珊瑚宫心海', position3: '那维莱特', position4: '枫原万叶' } }] };
  const water = { ...group.projects[0].jsScriptSettingsObject, position2: '芙宁娜', position4: '琴' };
  updateGroup(group, true);
  assert.equal(group.projects.length, 9);
  assert.deepEqual(group.projects.find(p => p.name === '配置水').jsScriptSettingsObject, water);
  assert.deepEqual(group.config.taskOrder, ['b', 'a']);
  for (const name of ['矿物', '采集']) assert.equal(presets[name].filter(c => ['琴', '枫原万叶'].includes(c)).length, 2);
  const once = JSON.stringify(group); updateGroup(group, true); assert.equal(JSON.stringify(group), once);
});

test('route-specific utility choices and grass slot are corrected without changing selections or AutoPlan', () => {
  const group = { projects: [
    { name: 'CD采集水晶块', folderName: 'CD-Aware-AutoGather', jsScriptSettingsObject: { selectRoute_x: ['keep'] } },
    { name: '养成采集：霜仙花', folderName: 'CD-Aware-AutoGather', jsScriptSettingsObject: {} },
    { folderName: 'HCY-FullyAutoAndSemiAutoTools', jsScriptSettingsObject: { team_seven_elements: 'wrong', treeLevel_1_1: ['keep'] } },
    { folderName: 'AutoPlan', jsScriptSettingsObject: { partyName: '水', key: 'untouched' } },
  ] };
  updateGroup(group);
  assert.equal(group.projects[0].jsScriptSettingsObject.partyName, '矿物');
  assert.equal(group.projects[1].jsScriptSettingsObject.partyName, '采集');
  assert.deepEqual(group.projects[0].jsScriptSettingsObject.selectRoute_x, ['keep']);
  assert.equal(group.projects[2].jsScriptSettingsObject.team_seven_elements, '矿物,火,水,风,雷,草,冰,岩');
  assert.deepEqual(group.projects[3].jsScriptSettingsObject, { partyName: '水', key: 'untouched' });
});

test('missing game preset rejects before editing the active party; async lifecycle is awaited', async () => {
  const original = '(async function () { await genshin.switchParty(settings.switchPartyName); editParty(); })();';
  const patched = guardSetupScript(original);
  assert.equal(guardSetupScript(patched), patched);
  let edited = false;
  await assert.rejects(vm.runInNewContext('(async()=>{' + patched + '})()', {
    settings: { switchPartyName: '采集' }, genshin: { switchParty: async () => false }, editParty: () => { edited = true; },
  }), /目标队伍不存在/);
  assert.equal(edited, false);
});
