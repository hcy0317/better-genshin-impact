const fs = require('node:fs');
const path = require('node:path');
const cp = require('node:child_process');
const crypto = require('node:crypto');
const presets = require('./element-party-presets.json');
const elements = '矿物,火,水,风,雷,草,冰,岩';

function updateGroup(group, setup = false) {
  if (setup) {
    const existing = group.projects.filter(p => p.folderName === 'AutoSwitchRoles');
    if (!existing.length) throw Error('No AutoSwitchRoles template');
    const managed = Object.entries(presets).map(([name, roles], i) => {
      const original = existing.find(p => p.jsScriptSettingsObject?.switchPartyName === name);
      const project = structuredClone(original ?? existing[0]);
      project.index = i + 1;
      project.name = '配置' + name;
      project.jsScriptSettingsObject = { ...project.jsScriptSettingsObject, switchPartyName: name };
      roles.forEach((role, index) => project.jsScriptSettingsObject['position' + (index + 1)] = role);
      return project;
    });
    group.projects = [...managed, ...group.projects.filter(p => p.folderName !== 'AutoSwitchRoles')];
  }
  for (const project of group.projects ?? []) {
    const settings = project.jsScriptSettingsObject;
    if (!settings) continue;
    if (project.folderName === 'CD-Aware-AutoGather') {
      const mining = /水晶|矿物|矿石/.test(project.name);
      settings.partyName = mining ? '矿物' : '采集';
      settings.partyName2nd = mining ? '采集' : '矿物';
    }
    if (['FullyAutoAndSemiAutoTools', 'HCY-FullyAutoAndSemiAutoTools'].includes(project.folderName)) {
      settings.team_seven_elements = elements;
    }
  }
  return group;
}

function guardSetupScript(source) {
  if (!source.includes('目标队伍不存在，停止配置以免改动当前队伍')) {
    const anchor = 'await genshin.switchParty(settings.switchPartyName);';
    if (source.split(anchor).length !== 2) throw Error('AutoSwitchRoles switch anchor changed');
    source = source.replace(anchor, `const switched = await genshin.switchParty(settings.switchPartyName);
        if (String(switched).toLowerCase() !== 'true') {
            throw new Error('目标队伍不存在，停止配置以免改动当前队伍：' + settings.switchPartyName);
        }`);
  }
  const entry = '(async function () {';
  if (!source.includes('await ' + entry)) {
    if (source.split(entry).length !== 2) throw Error('AutoSwitchRoles async entry changed');
    source = source.replace(entry, 'await ' + entry);
  }
  return source;
}

function sync(root, sourceRoot, apply = false) {
  root = fs.realpathSync(root);
  sourceRoot = fs.realpathSync(sourceRoot);
  const sourceMode = root === sourceRoot;
  const updates = [];
  function stage(file, after) {
    const before = fs.existsSync(file) ? fs.readFileSync(file) : null;
    const bytes = Buffer.isBuffer(after) ? after : Buffer.from(after, 'utf8');
    if (!before || !before.equals(bytes)) updates.push({ file, before, after: bytes });
  }
  const groups = path.join(root, 'User/ScriptGroup');
  for (const item of fs.readdirSync(groups, { withFileTypes: true })) {
    if (!item.isFile() || !item.name.endsWith('.json')) continue;
    const file = path.join(groups, item.name);
    const group = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
    const before = JSON.stringify(group);
    updateGroup(group, item.name === '手动-配置自动化队伍.json');
    if (JSON.stringify(group) !== before) stage(file, JSON.stringify(group, null, 2) + '\n');
  }
  // Runtime schemas and per-UID defaults are the next regeneration's input.
  for (const folder of ['FullyAutoAndSemiAutoTools', 'HCY-FullyAutoAndSemiAutoTools']) {
    for (const relative of ['settings.json', 'config/uidSettings.json']) {
      const file = path.join(root, 'User/JsScript', folder, relative);
      if (!fs.existsSync(file)) continue;
      const document = JSON.parse(fs.readFileSync(file, 'utf8').replace(/^\uFEFF/, ''));
      let changed = false;
      function visit(value) {
        if (!value || typeof value !== 'object') return;
        if (value.name === 'team_seven_elements' && value.default !== elements) {
          value.default = elements; changed = true;
        }
        Object.values(value).forEach(visit);
      }
      visit(document);
      if (changed) stage(file, JSON.stringify(document, null, 2) + '\n');
    }
  }
  // The user-approved roster now includes the revised Hydro party as well.
  for (const name of Object.keys(presets)) {
    stage(path.join(root, 'User/AutoFight', '00-' + name + '.txt'),
      fs.readFileSync(path.join(sourceRoot, 'User/AutoFight', '00-' + name + '.txt')));
  }
  const setupScript = path.join(root, 'User/JsScript/AutoSwitchRoles/main.js');
  if (fs.existsSync(setupScript)) stage(setupScript, guardSetupScript(fs.readFileSync(setupScript, 'utf8')));
  if (apply && !sourceMode && process.platform === 'win32') {
    const processes = cp.execFileSync('tasklist', ['/FI', 'IMAGENAME eq BetterGI.exe', '/FO', 'CSV', '/NH'], { encoding: 'utf8', windowsHide: true });
    if (/BetterGI\.exe/i.test(processes)) throw Error('BetterGI is running; no configuration was changed.');
  }
  if (apply && !sourceMode && updates.some(u => u.file.endsWith('.txt') && u.after.includes('refresh'))) {
    assertRefreshRuntime(root, sourceRoot);
  }
  for (const update of updates) {
    if (update.file.endsWith('.json')) JSON.parse(update.after.toString('utf8'));
    if (fs.existsSync(update.file) && fs.lstatSync(update.file).isSymbolicLink()) throw Error('Refusing symlink: ' + update.file);
  }
  let backup = null;
  if (apply && updates.length) {
    // Runtime backups are outside the source checkout; source-mode files are covered by git.
    backup = path.join(root, 'User/backup/element-party-presets', String(Date.now()));
    for (const update of updates) {
      if (update.before) {
        const saved = path.join(backup, path.relative(root, update.file));
        fs.mkdirSync(path.dirname(saved), { recursive: true });
        fs.writeFileSync(saved, update.before, { flag: 'wx' });
      }
    }
    for (const update of updates) {
      if (update.before && !fs.readFileSync(update.file).equals(update.before)) throw Error('Concurrent configuration edit: ' + update.file);
      fs.writeFileSync(update.file, update.after);
    }
  }
  return { applied: apply, backup, files: updates.map(u => path.relative(root, u.file)) };
}
function assertRefreshRuntime(root, sourceRoot) {
  // Older engines silently ignore the refresh argument. Never install the new scripts alone.
  const published = path.join(sourceRoot, 'bin/x64/Release/net8.0-windows10.0.22621.0/publish/win-x64/BetterGI.exe');
  const installed = path.join(root, 'BetterGI.exe');
  const codeFiles = ['GuardianSkillSwitchPolicy.cs', 'AutoFightTask.cs', 'AutoFightJsonTask.cs',
    'AutoFightSeek.cs', 'Script/CombatCommand.cs', 'Script/CombatScriptExecutor.cs', 'Model/Avatar.cs'];
  if (!fs.existsSync(published) || !fs.existsSync(installed) ||
      codeFiles.some(f => fs.statSync(path.join(sourceRoot, 'GameTask/AutoFight', f)).mtimeMs > fs.statSync(published).mtimeMs))
    throw Error('Publish and install the current refresh-capable BetterGI runtime before syncing these strategies.');
  const hash = file => crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');
  if (hash(published) !== hash(installed)) throw Error('Installed BetterGI does not match the refresh-capable publish output; no scripts were changed.');
}

module.exports = { updateGroup, guardSetupScript, presets, sync, assertRefreshRuntime };
if (require.main === module) {
  const [root, sourceRoot, flag] = process.argv.slice(2);
  if (!root || !sourceRoot) throw Error('Usage: node sync-element-party-presets.cjs <BetterGI root> <source BetterGenshinImpact directory> [--apply]');
  console.log(JSON.stringify(sync(root, sourceRoot, flag === '--apply'), null, 2));
}
