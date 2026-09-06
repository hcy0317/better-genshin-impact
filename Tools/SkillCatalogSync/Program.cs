using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Newtonsoft.Json;
using System.Net.Http;
using System;
using System.IO;
using System.Linq;
using System.Threading;

// 仅操作技能数据，不启动 BetterGI 或游戏；旧 <数据库> [角色键...] 同步写法继续兼容。
if (args.Length < 1) throw new ArgumentException("用法：SkillCatalogSync <数据库绝对路径> [sync [角色键...] | status | show [角色键...] | import-rules 文件 | import-profile 文件 | override 技能ID 数值项 文件 原因 | remove-override 技能ID 数值项 | remove-profile 角色键 | rules [版本]]");
if (!Path.IsPathFullyQualified(args[0])) throw new ArgumentException("数据库路径必须为绝对路径");
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var seedPath = Path.Combine(AppContext.BaseDirectory, "GameTask", "AutoFight", "Assets", "SkillData", "builtin-skills.json");
var catalog = new CombatSkillCatalog(new SkillCatalogStore(args[0]), new GenshinDbSkillSource(client), seedPath);
void Require(int count) { if (args.Length != count) throw new ArgumentException("参数数量错误：" + args.ElementAtOrDefault(1)); }
object? result;
switch (args.ElementAtOrDefault(1))
{
    case "status":
        Require(2); result = catalog.ReadStatus(); break;
    case "show":
        result = catalog.Store.ReadSnapshot().Skills.Values.Where(skill => args.Length == 2 || args.Skip(2).Contains(skill.CharacterKey)).ToArray(); break;
    case "profiles":
        Require(2); result = catalog.Store.ReadSnapshot().Profiles; break;
    case "rules":
        if (args.Length > 3) throw new ArgumentException("rules 只接收一个可选版本号");
        result = catalog.Store.ReadRulePack(args.ElementAtOrDefault(2)); break;
    case "import-rules":
        Require(3); result = catalog.ImportRules(CombatSkillCatalog.ReadLocalJsonFile(args[2])); break;
    case "import-profile":
        Require(3); result = catalog.ImportProfile(CombatSkillCatalog.ReadLocalJsonFile(args[2])); break;
    case "override":
        Require(6); result = catalog.SetMetricOverride(args[2], args[3], CombatSkillCatalog.ReadLocalJsonFile(args[4]), args[5]); break;
    case "remove-override":
        Require(4); catalog.Store.RemoveMetricOverride(args[2], args[3]); result = catalog.ReadStatus(); break;
    case "remove-profile":
        Require(3); catalog.Store.RemoveCharacterProfile(args[2]); result = catalog.ReadStatus(); break;
    default:
        var keys = args.Skip(args.ElementAtOrDefault(1) == "sync" ? 2 : 1).ToArray();
        await catalog.SyncAsync(keys.Length == 0 ? null : keys,
            new Progress<SkillSyncProgress>(p => Console.Error.WriteLine($"{p.Completed}/{p.Total}: {p.CharacterKey}")), cts.Token);
        result = catalog.ReadStatus(); break;
}
Console.WriteLine(JsonConvert.SerializeObject(result));
