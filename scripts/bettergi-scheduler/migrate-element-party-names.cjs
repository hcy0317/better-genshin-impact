// Bounded, reversible migration. Default is dry-run; never touches run records.
const fs = require('node:fs');
const path = require('node:path');
const cp = require('node:child_process');

const names = Object.freeze({
  '钟班香万': '火', '钟心那万': '水', '钟芙琴万': '风', '钟纳久万': '雷',
  '钟心纳久': '草', '钟迪绫万': '冰', '钟心娜万': '岩', '钟纳娜万': '矿物',
});
function renameText(text) {
  for (const [oldName, newName] of Object.entries(names)) text = text.replaceAll(oldName, newName);
  return text;
}
function migrate(root, { apply = false, keysOnly = false, source = false } = {}) {
  root = fs.realpathSync(root);
  const user = path.join(root, 'User');
  if (!fs.statSync(user).isDirectory()) throw Error('Missing User directory');
  const updates = [];
  const renames = [];
  const consider = (file, transform) => {
    if (!fs.existsSync(file)) return;
    if (fs.lstatSync(file).isSymbolicLink()) throw Error(`Refusing link: ${file}`);
    const before = fs.readFileSync(file, 'utf8');
    const after = transform(before);
    if (after !== before) updates.push({ file, before, after });
  };
  if (!keysOnly) {
    consider(path.join(user, 'config.json'), renameText);
    const groups = path.join(user, 'ScriptGroup');
    if (fs.existsSync(groups)) for (const entry of fs.readdirSync(groups, { withFileTypes: true })) {
      if (entry.isFile() && entry.name.endsWith('.json')) consider(path.join(groups, entry.name), renameText);
    }
    // Only named configuration surfaces, not record/cache/history or route files.
    for (const relative of [
      'JsScript/HCY-FullyAutoAndSemiAutoTools/settings.json',
      'JsScript/HCY-FullyAutoAndSemiAutoTools/config/uidSettings.json',
      'JsScript/FullyAutoAndSemiAutoTools/settings.json',
      'JsScript/FullyAutoAndSemiAutoTools/config/uidSettings.json',
      'JsScript/AutoCommissionNova/Data/user-config/102550550.json',
      'JsScript/HCY-AutoCommission/Data/user-config.json',
      'JsScript/HCY-AutoCommission/Data/user-config/102550550.json',
    ]) consider(path.join(user, relative), renameText);
    for (const [oldName, newName] of Object.entries(names)) {
      const from = path.join(user, 'AutoFight', `00-${oldName}.txt`);
      const to = path.join(user, 'AutoFight', `00-${newName}.txt`);
      if (!fs.existsSync(from)) continue;
      if (fs.lstatSync(from).isSymbolicLink() || fs.existsSync(to)) throw Error(`Rename conflict: ${to}`);
      renames.push({ from, to });
    }
  }
  const manifest = path.join(user, 'JsScript', 'AutoPlan', 'manifest.json');
  if (!source) {
    const key = JSON.parse(fs.readFileSync(manifest, 'utf8').replace(/^\uFEFF/, '')).key;
    if (typeof key !== 'string' || !key.trim()) throw Error('AutoPlan manifest has no key');
    const groupFile = path.join(user, 'ScriptGroup', '养成一条龙-102550550.json');
    const pending = updates.find(u => u.file === groupFile);
    const syncGroup = text => {
      const group = JSON.parse(text.replace(/^\uFEFF/, ''));
      let changed = false;
      for (const project of group.projects ?? []) if (project.folderName === 'AutoPlan') {
        if (!project.jsScriptSettingsObject) throw Error('AutoPlan project settings missing');
        if (project.jsScriptSettingsObject.key !== key.trim()) {
          project.jsScriptSettingsObject.key = key.trim(); changed = true;
        }
      }
      return changed ? JSON.stringify(group, null, 2) + '\n' : text;
    };
    if (pending) pending.after = syncGroup(pending.after); else consider(groupFile, syncGroup);
    consider(path.join(user, 'JsScript', 'AutoPlan', 'settings.json'), text => {
      const settings = JSON.parse(text.replace(/^\uFEFF/, ''));
      const field = settings.find(s => s.name === 'key');
      if (!field) throw Error('AutoPlan key setting missing');
      if (field.default === key.trim()) return text;
      field.default = key.trim();
      return JSON.stringify(settings, null, 2) + '\n';
    });
  }
  // Verify every JSON before creating a backup or changing any target.
  for (const update of updates) JSON.parse(update.after.replace(/^\uFEFF/, ''));
  if (apply && !source && !keysOnly && process.platform === 'win32') {
    const running = cp.execFileSync('tasklist', ['/FI', 'IMAGENAME eq BetterGI.exe', '/FO', 'CSV', '/NH'], { encoding: 'utf8', windowsHide: true });
    if (/BetterGI\.exe/i.test(running)) throw Error('Close BetterGI before applying party-name migration; no process was stopped.');
  }
  let backup = null;
  if (apply && (updates.length || renames.length)) {
    const backupParent = source && fs.existsSync(path.join(root, '..', 'BetterGenshinImpact.sln')) ? path.dirname(root) : root;
    backup = path.join(backupParent, source ? '.codex-tmp' : '.codex-backups', `element-party-${Date.now()}`);
    for (const file of [...updates.map(u => u.file), ...renames.map(r => r.from)]) {
      const destination = path.join(backup, path.relative(root, file));
      fs.mkdirSync(path.dirname(destination), { recursive: true });
      fs.copyFileSync(file, destination, fs.constants.COPYFILE_EXCL);
    }
    for (const update of updates) {
      if (fs.readFileSync(update.file, 'utf8') !== update.before) throw Error(`Configuration changed during migration: ${update.file}`);
      fs.writeFileSync(update.file, update.after, 'utf8');
    }
    for (const rename of renames) fs.renameSync(rename.from, rename.to);
  }
  return { applied: apply, backup, updates: updates.map(u => path.relative(root, u.file)), renames: renames.map(r => ({ from: path.relative(root, r.from), to: path.relative(root, r.to) })) };
}
module.exports = { names, renameText, migrate };
if (require.main === module) {
  const args = process.argv.slice(2);
  if (!args[0] || args[0].startsWith('--')) throw Error('Usage: node migrate-element-party-names.cjs <BetterGI root> [--apply] [--keys-only|--source]');
  console.log(JSON.stringify(migrate(args[0], { apply: args.includes('--apply'), keysOnly: args.includes('--keys-only'), source: args.includes('--source') }), null, 2));
}
