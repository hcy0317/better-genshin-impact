# 战斗技能知识库

设置入口：自动战斗 → 技能知识库（离线与同步）。首次读取自动载入随程序分发的基础事实和机制规则；不会联网。运行期数据库为 `User/CombatSkills/skills.db`，每场战斗只读取一次快照。

## 数据边界

- 数值来源是固定提交的 [genshin-db](https://github.com/theBowja/genshin-db)。同步先取得提交号，再下载整批英文、中文和逐等级数值。下载、校验、取消或事务失败保留上一有效批次；只替换本次请求的角色。
- `builtin-skills.json` 保存原始事实；`builtin-mechanics.json` 保存独立的输入形态、主效果和刷新关系。规则保存所有相关原文、标签和参数映射的指纹。来源变化或依赖缺失时标记 `needs-review`，不沿用刷新授权。数值曲线改变但含义映射不变可以继续使用。
- “来源匹配”是资料校验，不是实机验证。护盾、标记、范围和后台效果默认是施放推算的时间窗口，不证明本体、命中或当前目标仍有效。未知产球条件/数量不填零；最大充能次数不是当前可用次数。
- 个人角色条件和数值修正单独保存，不被同步替换。未知被动不自动视为已解锁。`Equipment` 仅保存说明，不自动推导装备效果。
- 适用的人工修正优先，其次数据库，最后策略内 `timing`。每字段来源和冲突可查询；所有更新只影响下一场。纯时间声明不能授权关系式刷新。

## 命令行

所有命令只操作数据，不启动 BetterGI 或游戏。数据库路径必须为绝对路径。

```powershell
dotnet run --project Tools/SkillCatalogSync -- 'C:\data\combat-skills.db' sync zhongli navia sangonomiyakokomi
dotnet run --project Tools/SkillCatalogSync -- 'C:\data\combat-skills.db' status
dotnet run --project Tools/SkillCatalogSync -- 'C:\data\combat-skills.db' show navia
dotnet run --project Tools/SkillCatalogSync -- 'C:\data\combat-skills.db' import-rules 'C:\data\reviewed-mechanics.json'
dotnet run --project Tools/SkillCatalogSync -- 'C:\data\combat-skills.db' import-profile 'C:\data\navia-profile.json'
dotnet run --project Tools/SkillCatalogSync -- 'C:\data\combat-skills.db' profiles
```

`sync` 后不传角色键表示全部公开角色。旧 `<数据库> [角色键...]` 写法保持兼容。`rules [版本]` 可查询当前或历史规则包；相同版本号不能覆盖不同内容。规则更新须人工核验后使用新版本号导入，不自动重新计算指纹冒充已复核。

个人覆盖最小示例（只填写自己已确认的条件，不要照抄成真实角色状态）：

```json
{"CharacterKey":"navia","UnlockedPassives":["navia.passive1"],"Reason":"已在角色天赋页确认解锁"}
```

可选字段：`Ascension`（0–6）、`Constellation`（0–6）、`TalentLevels`（如 `{"e":8,"q":8}`，1–15）、`Equipment`。娜维娅附魔和心海水母刷新分别要求已确认 `navia.passive1`、`sangonomiyakokomi.passive1`；未知时原策略可使用短输出或 E 重建等替代段。它们不是每队需要填写的新技能 JSON。

人工数值修正：`override 技能ID 数值项 数值文件 原因`，数值文件例 `{"Unit":"seconds","Values":[12]}`。`remove-override 技能ID 数值项` 撤销该人工值；`remove-profile 角色键` 撤销个人条件。不会删除来源历史。

## 现有脚本 API

`dispatcher` 增加以下方法；查询与更新返回 JSON 字符串，按需 `JSON.parse(...)`。不需要创建另一个执行器。

- `SyncCombatSkills(characterKeys?, customCt?)`：异步同步，英文角色键逗号分隔，继承脚本取消令牌。
- `ReadCombatSkillStatus()` / `ReadCombatSkillFacts(characterKey?)` / `ReadCombatSkillProfiles()`：状态、来源事实和个人条件。
- `ImportCombatSkillRules(json)` / `ImportCombatSkillProfile(json)`：导入经过严格字段、版本和大小校验的数据。
- `SetCombatSkillMetricOverride(skillId, metric, valueJson, reason)`：有原因的数值修正。

上述名称为 C# 宿主方法名，具体脚本引擎沿用原有成员命名规则。API 接受 JSON 内容，不提供任意本地文件读取或任意同步 URL。
