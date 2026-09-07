# gcsim 实物配装计算桥接

这是新版配装工程的计算基础，包含真实背包的单角色多 Build 和多角色联合搜索。网页版管理及穿戴执行由 Better Genshin Tools/BetterGI 集成层负责。它按需启动一个受限子进程，使用固定版本 gcsim 计算；不运行 BetterGI、原神或 RDP，不发送游戏输入，不修改装备或锁定状态。

## 构建与调用

开发需要 Go 1.27；`go.mod` 固定引擎及工具链，运行编译后的程序无需 Go 或外网。

```powershell
go test -vet=off -p 2 ./... -count=1 -timeout 45s
go vet -p 2 ./...
go build -p 2 -o bin/gcsim-bridge.exe ./cmd/gcsim-bridge
./bin/gcsim-bridge.exe --capabilities
Get-Content ./examples/single-character.json -Raw | ./bin/gcsim-bridge.exe
```

示例数据全部是合成夹具，不对应真实账号。每次调用在 stdin 接收一个 JSON，在 stdout 返回一个 JSON；上游诊断走 stderr。退出码 0 表示计算成功，不等于硬约束通过或可以穿戴。`report.validation.state` 才是本批次约束状态。

Windows 承载端使用 Job Object，在发送配置前设置并回读内存、单进程、低优先级及随父句柄关闭终止的边界。每个 worker 使用一个 Go 处理器；超时、取消和输出超限均结束自己的计算单元。默认墙钟 30 秒、worker 内存 512 MiB、合计输出 16 MiB。父进程输入最多 2 MiB，输出缓存也受上述字节上限约束。内存异常退出不会被伪装为可行结果。其他平台尚不提供同等隔离，执行入口明确拒绝。

## 输入与结果合同

- `evaluation.config` 是完整 gcsim 场景：角色及武器等级、命座、天赋、敌人、循环等沿用其原生语法。原生配置不是“已扫描真实个人条件”的证明。`seeds` 必须明确、非空且不重复，最多 64 条。
- `inventory` 引用既有扫描身份，不建立第二个背包。`equipment` 只传本次解析的装备；ScanIndex 仅在该扫描快照内有意义。五部位、实物互斥、词条与套装会验证。
- 库存词条采用 BetterGI/GOOD 键，百分比值使用百分数，例如 `critRate_=31.1`。主词条数值必须先由调用方的版本化目录解析；现有扫描 DTO 只有主词条键和等级，不能把缺失数值默认为零。目标角色的原生 `add stats`/`add set` 必须移除，防止与库存重复。当前不擅自处理休眠词条。
- 未提供 `rounds` 时，平均 DPS 直接采用上游完整模拟结果。显式计分窗口采用 `[startFrame,endFrame)`，平均 DPS 使用窗口内整队实际伤害除以总窗口时长，暖机或窗口间空隙不计分。`meanDpsSource` 区分两种口径。帧率为 60。
- 原始轨迹、角色/武器条件、指标统计、逐轨迹逐轮约束、能量边界和 Buff 激活事件分别返回。角色伤害/有效治疗先按窗口提取原始值，再跨样本算术平均；本层不做参照归一化、封顶或角色权重排序。
- `validation.state` 为 `passed` / `failed` / `indeterminate`。空批次、漏种子、截断轮次、缺指标不通过；任一明确违反保持失败。这个结论只覆盖声明的有限模拟样本，不证明所有随机情况或实机可靠性。
- `inputSha256` 绑定完整请求；缓存还须绑定引擎、适配器与统计口径。搜索与最终独立复评批次的选择、基线资格及联合分配由后续 T03/T04 负责。

## 手动 Buff 能力

可用类型：原生属性、减抗、减防、限定普攻/技能/爆发直伤增伤。原生分数单位的 `0.2` 表示 20%；平值使用 `flat`。`resistance` 的负值表示减抗；`defense_reduction` 使用 0..1 正数。

支持开局、明确声明的轮次边界、指定角色实际 `OnActionExec` 事件三个锚。动作锚目前支持 attack/skill/burst，不回溯已经快照的攻击。时长为固定模拟帧，不自动附加 hitlag 延长；相同 ID 重触发刷新，不堆叠。敌人效果作用于触发时已存在的敌人。常驻减抗/减防被映射到本次模拟期限，以兼容所固定上游版本的过期判断。

`relationship` 可为 `additional` 或 `pending_review`（默认）；没有通用的原生替代或自动去重能力，`replace` 明确拒绝。`coverageRatio` 只支持开局常驻属性，且明确标记近似：时间覆盖比例不是伤害受益比例。所有手动假设保留在结果中。

`--capabilities` 给出实际适配矩阵和上游已知键。已知名称不保证任意配置可运行；上游标记不完整的角色默认拒绝，显式 `allowPartial` 才可带标记试算。附加基础伤害、通用反应增幅、可靠护盾约束、原生效果替代尚未开放，不能靠填写字段伪造支持。

## 维护边界

`--catalog` 导出与当前引擎相同版本的角色、武器、套装及技能数值和游戏 ID，供网页、Enka 映射及战斗数据补充使用。条目存在不等于所有机制完整支持。

`--optimize` 接收 `{ "optimization": Request, "limits": Budget }`，在同一受限 worker 内完成联合搜索。Request 使用 `schemaVersion=1`，引用既有 `inventory`，并携带 `items`、`characters`、`scenarios`、独立的 `searchSeeds`/`validationSeeds`、`evaluationBudget`。角色 `current`、`fixedSlots`、`mainStats`、`requiredSets`、`minimumStats` 和保护属于硬约束。场景 `participants` 指定本场景参与的优化角色，`fixedEquipment` 预留真实固定队友的五件实物。

三档分别为 `balanced`（去重场景加权实际 DPS）、`peak`（角色目标保留率）、`fallback`（完整加权短缺向量词典序）。零参照可由本次有预算搜索批次中合格观测生成，搜索结束后冻结、再统一排名，不从最终验证样本更新；它不是理论上界。百分比初始属性下限使用 GOOD 百分数，取帧零面板，不冒充战斗期间覆盖率。

`exact=true` 在预算内完整枚举；`false` 使用全池替换、同部位互换及确定种子重启，不承诺全局最优。预算耗尽、未知指标、取消、最终验证失败和证明无解分别返回。合格旧装保留；不合格旧装不阻止合格但 DPS 较低的方案。结果包含每人五件、各场景独立复评、全部供装影响、冻结参照。均衡的小幅提升采用配对独立样本区间，证据不足返回 `feasible_uncertain`；保尖/兜底不伪造统计置信度。

角色、武器、套装、反应与动态机制来自 gcsim 模块，不复制维护整套旧伤害数据库。升级须一起调整固定版本并重跑对齐、时序、约束与强停回归；不能混用新引擎和旧缓存。本目录不提供后台自动更新、发布或安装操作。后续上游更新闭环属于 T09。

计算核心测试不等于完整网页或实机验收。角色/Enka 管理、确认穿戴和战斗循环适配仍按已接受切片推进。实机验证与部署需要独立授权。
