using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using BetterGenshinImpact.GameTask.AutoFight.Config;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

public sealed record CombatTiming(double? Cooldown, double? Duration, string Source = "strategy",
    string? CooldownSource = null, string? DurationSource = null);

internal sealed class CombatFlowBlock(string name)
{
    public string Name { get; } = name;
    public CombatCommand? Declaration { get; init; }
    public CombatCommand? Completion { get; set; }
    public FormatException Error(string message) => (Declaration ?? Completion ?? Nodes.FirstOrDefault()?.Command)?.Error(message)
        ?? CombatCommand.SyntaxError(message, 1, 1);
    public List<CombatFlowNode> Nodes { get; } = [];
    public string? CompletionRecord { get; set; }
    public ConditionEvaluator.CompiledCondition? Requires { get; init; }
    public string? OnFail { get; init; }
    public bool Atomic { get; init; }
    public double Timeout { get; init; } = double.PositiveInfinity;
    public double EstimatedSeconds { get; set; }
    public HashSet<string> CoverageRecords { get; } = new(StringComparer.Ordinal);
    public HashSet<string> EntryCoverageRecords { get; } = new(StringComparer.Ordinal);
    public HashSet<string> ProducedRecords { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, int[]> WatchPoints { get; } = new(StringComparer.Ordinal);
}

internal sealed class CombatFlowNode(CombatCommand command)
{
    public CombatCommand Command { get; } = command;
    public CombatFlowBlock? Block { get; init; }
    public ConditionEvaluator.CompiledCondition? Condition { get; init; }
}

/// <summary>只读编译计划可复用；运行记录和调用栈不得保存在计划中。</summary>
public sealed partial class CombatFlowProgram
{
    internal CombatFlowBlock Root { get; } = new("$root");
    internal Dictionary<string, CombatFlowBlock> Blocks { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, CombatTiming> Timings { get; } = new(StringComparer.Ordinal);
    internal HashSet<string> RecordNames { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<CombatCommand, CombatTiming> _resolvedTimings = new();
    private readonly Dictionary<CombatCommand, CombatRecordSource> _recordSources = new();
    private readonly Dictionary<CombatCommand, (string EffectId, string ProducerSkillId, string ProducerRevision)[]> _refreshes = new();
    private readonly Dictionary<CombatCommand, CombatCommand> _feeds = new();
    private readonly List<string> _diagnostics = [];
    public IReadOnlyList<string> Diagnostics => _diagnostics;
    public IReadOnlyCollection<string> Actors { get; private set; } = Array.Empty<string>();
    public bool Loop { get; private set; }
    internal void AllowHostLoop(bool loop) => Loop |= loop;

    public static CombatFlowProgram Compile(string text, SkillCatalogSnapshot? database = null) => Compile(CombatScriptParser.ParseContext(text), database);

    public static CombatFlowProgram Compile(CombatScript script, SkillCatalogSnapshot? database = null)
    {
        script = new(new HashSet<string>(script.AvatarNames), script.CombatCommands.Select(command => new CombatCommand(command)).ToList());
        CombatFlowCompatibility.Compile(script.CombatCommands);
        foreach (var command in script.CombatCommands) CombatParameterRegistry.Validate(command);
        var program = new CombatFlowProgram { Actors = script.AvatarNames.ToArray() };
        var stack = new Stack<CombatFlowBlock>();
        stack.Push(program.Root);
        var executableSeen = false;
        foreach (var command in script.CombatCommands)
        {
            if (command.Method == Method.Timing)
            {
                if (executableSeen || command.Args?.Count != 1) throw command.Error("timing 必须在策略开头声明一个名称");
                var timing = new CombatTiming(Number(command, "cd"), Number(command, "duration", positive: true));
                if (!program.Timings.TryAdd(command.Args[0], timing)) throw command.Error("重复 timing：" + command.Args[0]);
                continue;
            }
            if (command.Method == Method.Strategy)
            {
                if (executableSeen || command.Options.GetValueOrDefault("loop") != "battle")
                    throw command.Error("strategy(loop=battle) 必须位于策略开头");
                program.Loop = true;
                continue;
            }
            executableSeen = true;
            if (command.Method == Method.Segment && command.Args?.FirstOrDefault() == "end")
            {
                if (stack.Count == 1) throw command.Error("segment(end) 缺少开始位置");
                var completed = stack.Pop();
                completed.CompletionRecord = command.Options.GetValueOrDefault("record");
                completed.Completion = command;
                continue;
            }
            var node = new CombatFlowNode(command)
            {
                Condition = CompileCondition(command, "if")
            };
            if (command.Method == Method.Segment)
            {
                if (command.Args?.FirstOrDefault() != "start") throw command.Error("segment 需要 start/end");
                var name = command.Options.GetValueOrDefault("name") ?? "$inline" + script.CombatCommands.IndexOf(command);
                var block = new CombatFlowBlock(name)
                {
                    Declaration = command,
                    Requires = CompileCondition(command, "requires"),
                    OnFail = command.Options.GetValueOrDefault("onfail"),
                    Atomic = command.HasFlag("atomic"),
                    Timeout = command.Options.ContainsKey("timeout") || command.HasFlag("atomic")
                        ? CombatFlowPolicy.Timeout(command, CombatFlowPolicy.AtomicSeconds) : double.PositiveInfinity
                };
                if (!program.Blocks.TryAdd(name, block)) throw command.Error("重复片段：" + name);
                if (command.Args.Contains("define") && !command.Options.ContainsKey("name")) throw command.Error("define 片段必须命名");
                node = new(command) { Block = block, Condition = node.Condition };
                if (!command.Args.Contains("define")) stack.Peek().Nodes.Add(node);
                stack.Push(block);
            }
            else stack.Peek().Nodes.Add(node);
        }
        if (stack.Count != 1) throw stack.Peek().Error("segment 缺少 end");
        foreach (var block in program.Blocks.Values.Append(program.Root))
            foreach (var node in block.Nodes)
            {
                if (node.Command.Options.TryGetValue("timing", out var name) && !program.Timings.ContainsKey(name))
                    throw node.Command.Error("未声明 timing：" + name);
                if (node.Command.Method == Method.Call || node.Command.Method == Method.JumpTo)
                {
                    var target = node.Command.Args![0];
                    if (!program.Blocks.ContainsKey(target)) throw node.Command.Error("未声明片段：" + target);
                }
                if (node.Command.Method == Method.Branch)
                {
                    if (node.Condition == null || !node.Command.Options.ContainsKey("then")) throw node.Command.Error("branch 需要 if 和 then");
                    foreach (var key in new[] { "then", "else", "unknown" })
                        if (node.Command.Options.TryGetValue(key, out var target) && !program.Blocks.ContainsKey(target))
                            throw node.Command.Error("未声明分支片段：" + target);
                }
            }
        foreach (var block in program.Blocks.Values.Append(program.Root))
            foreach (var group in block.Nodes.Select((node, index) => (node, index))
                         .Where(item => item.node.Command.Options.ContainsKey("watch"))
                         .GroupBy(item => item.node.Command.Options["watch"], StringComparer.Ordinal))
                block.WatchPoints.Add(group.Key, group.Select(item => item.index).ToArray());
        CombatFlowValidation.Validate(program, script);
        foreach (var command in script.CombatCommands)
        {
            // 旧强制长 E 在编译边界降为公共 required + wait，不再有角色专用计时或执行器。
            if (command.Method == Method.Skill && command.Args?.Remove("refresh") == true)
                command.Flags.Add("required");
            if (command.Options.TryGetValue("feed", out var receiver))
            {
                receiver = DefaultAutoFightConfig.AvatarAliasToStandardName(receiver);
                command.Options["feed"] = receiver;
                program._feeds[command] = new(receiver, "wait(" + CombatFlowPolicy.FeedWaitSeconds.ToString(CultureInfo.InvariantCulture) + ")");
            }
            var fallback = command.Options.TryGetValue("timing", out var timingName) ? program.Timings[timingName] : null;
            var slot = command.Method == Method.Skill ? "e" : command.Method == Method.Burst ? "q" : null;
            var fact = slot == null ? null : database?.Skills.Values.SingleOrDefault(skill => skill.Character == command.Name && skill.Slot == slot);
            double? Metric(string key) => fact?.Metrics.ContainsKey(key) == true ? database!.Resolve(fact.Id, key) : null;
            var form = fact?.Forms.GetValueOrDefault(command.HasFlag("hold") ? "hold" : "press");
            var effect = command.Options.TryGetValue("effect", out var selectedEffect)
                ? form?.Effects.SingleOrDefault(value => value.Id == selectedEffect)
                : form?.DefaultEffect != null ? form.Effects.Single(value => value.Id == form.DefaultEffect)
                : form?.Effects.Count == 1 ? form.Effects[0] : null;
            if (selectedEffect != null && effect == null)
                throw command.Error("所选输入形态没有已知效果：" + selectedEffect);
            if (form?.Effects.Count > 1 && effect == null && command.Options.ContainsKey("record"))
                throw command.Error("技能包含多个效果，请使用 effect=效果名：" + command.Name);
            if (effect != null && !database!.IsApplicable(fact!.CharacterKey, effect.MinimumAscension, effect.MinimumConstellation, effect.RequiredPassiveSkillId))
            {
                program._diagnostics.Add($"{command.Name} 的效果 {effect.Id} 缺少已确认的个人适用条件；不授予该机制能力");
                effect = null;
            }
            var shapeCd = command.HasFlag("hold") ? "hold-cd" : "press-cd";
            var cooldownKey = form?.CooldownMetric ?? (fact?.Metrics.ContainsKey(shapeCd) == true ? shapeCd
                : fact?.Metrics.ContainsKey("cd") == true ? "cd" : null);
            var cooldown = cooldownKey == null ? null : Metric(cooldownKey);
            var durationFact = effect?.DurationSkillId is { } durationSkillId ? database!.Skills[durationSkillId] : fact;
            var durationKey = effect?.DurationMetric ?? (fact?.Metrics.ContainsKey("duration") == true ? "duration" : null);
            var duration = durationKey != null && durationFact != null ? database!.Resolve(durationFact.Id, durationKey) : (double?)null;
            string? FieldSource(SkillFact? source, string? metric) => source != null && metric != null && source.Metrics.TryGetValue(metric, out var value)
                ? value.SourceKind == "user-override" ? "user-override:" + source.Id + "/" + metric
                : "database:" + source.Revision + "/" + source.Id + "/" + metric : null;
            foreach (var (key, actual, declared) in new[] { ("cd", cooldown, fallback?.Cooldown), ("duration", duration, fallback?.Duration) })
                if (actual != null && declared != null && actual != declared)
                    program._diagnostics.Add($"{command.SourceFile ?? "策略"}:{command.SourceLine} {command.Name} {key} 使用覆盖/数据库值 {actual.Value.ToString(CultureInfo.InvariantCulture)}，而非 timing 声明 {declared.Value.ToString(CultureInfo.InvariantCulture)}");
            if (cooldown != null || duration != null || fallback != null)
                program._resolvedTimings[command] = new(cooldown ?? fallback?.Cooldown, duration ?? fallback?.Duration,
                    duration != null ? "database:" + durationFact!.Revision : fallback?.Source ?? "database:" + fact!.Revision,
                    FieldSource(fact, cooldownKey) ?? (fallback?.Cooldown != null ? "strategy:" + timingName : null),
                    FieldSource(durationFact, durationKey) ?? (fallback?.Duration != null ? "strategy:" + timingName : null));
            program._recordSources[command] = new(command.Name, command.Method.Alias[0], effect?.Id,
                fact?.Revision, fact?.Id, effect?.Capability, effect?.EndsOnSwitch ?? false, effect?.Scope);
            if (form != null)
                program._refreshes[command] = form.Refreshes
                    .Where(relation => relation.Verified && relation.Revision == fact!.Revision &&
                        database!.IsApplicable(fact.CharacterKey, relation.MinimumAscension, relation.MinimumConstellation, relation.RequiredPassiveSkillId) &&
                        !string.IsNullOrWhiteSpace(relation.SourceUrl) && !string.IsNullOrWhiteSpace(relation.EffectId) &&
                        !string.IsNullOrWhiteSpace(relation.ProducerSkillId) && !string.IsNullOrWhiteSpace(relation.ProducerRevision))
                    .Select(relation => (relation.EffectId, relation.ProducerSkillId, relation.ProducerRevision)).ToArray();
            if (command.Options.ContainsKey("refresh") && program._refreshes.GetValueOrDefault(command)?.Length is not > 0)
                program._diagnostics.Add($"{command.Name} 的关系式刷新缺少当前版本/个人条件支持；运行时跳过该刷新并保留替代分支");
        }
        program.ValidateRecordCapabilities(script);
        program.Actors = program.Actors.Concat(program._feeds.Values.Select(command => command.Name)).Distinct().ToArray();
        program.CompileRecharge();
        program.CompileBlockBudgets();
        return program;
    }

    private void ValidateRecordCapabilities(CombatScript script)
    {
        foreach (var group in script.CombatCommands
                     .Where(command => command.Options.ContainsKey("record") || command.Method == Method.Record)
                     .GroupBy(command => CombatFlowContext.NormalizeName(command.Method == Method.Record
                         ? command.Args![0] : command.Options["record"]), StringComparer.Ordinal))
        {
            var sources = group.Select(command => _recordSources.GetValueOrDefault(command)).ToArray();
            var known = sources.FirstOrDefault(source => source?.Effect != null);
            if (known == null) continue;
            var capability = known.Capability is { Length: > 0 } ? known.Capability : known.Effect;
            if (sources.Any(source => source?.Effect == null ||
                    (source.Capability is { Length: > 0 } ? source.Capability : source.Effect) != capability))
                throw group.Last().Error("同名记录的生成端效果能力不兼容：" + group.Key);
        }
    }

    internal CombatTiming? Timing(CombatCommand command) => _resolvedTimings.GetValueOrDefault(command);
    internal CombatRecordSource RecordSource(CombatCommand command) => _recordSources[command];
    internal CombatCommand? Feed(CombatCommand command) => _feeds.GetValueOrDefault(command);
    internal bool CanRefresh(CombatCommand command, CombatFlowRecord record) => record.Source?.Effect != null &&
        _refreshes.TryGetValue(command, out var relations) && relations.Any(relation =>
            relation.EffectId == record.Source.Effect && relation.ProducerSkillId == record.Source.SkillId &&
            relation.ProducerRevision == record.Source.Revision);

    internal double CoverageAfter(CombatFlowBlock block, int index, string record)
    {
        // 只看到下一个实际动作/安全边界；不会把整条主轴都算作不可中断窗口。
        for (; index < block.Nodes.Count; index++)
        {
            var node = block.Nodes[index];
            if (node.Command.Method == Method.Record) continue;
            var target = node.Block ?? (node.Command.Method == Method.Call ? Blocks[node.Command.Args![0]] : null);
            if (target != null) return target.Atomic && target.EntryCoverageRecords.Contains(record)
                ? target.EstimatedSeconds + CombatFlowPolicy.RecoverySeconds : CoverageAfter(target, 0, record);
            return node.Command.Options.GetValueOrDefault("keep") == record
                ? CombatFlowPolicy.CoverageSeconds(node.Command) : 0;
        }
        return 0;
    }

    internal double EstimateRemaining(CombatFlowBlock block, int index)
    {
        double seconds = 0;
        string? actor = null;
        foreach (var node in block.Nodes.Skip(index))
        {
            var target = node.Block ?? (node.Command.Method == Method.Call ? Blocks[node.Command.Args![0]] : null);
            if (target != null) { seconds += target.EstimatedSeconds; actor = null; }
            else if (node.Command.Method == Method.Branch)
            {
                var choice = node.Condition!.EvaluateBoolean((_, _) => null);
                var keys = choice == true ? new[] { "then" } : choice == false ? ["else"] : new[] { "then", "else", "unknown" };
                seconds += keys.Max(key => node.Command.Options.TryGetValue(key, out var name) ? Blocks[name].EstimatedSeconds : 0);
                actor = null;
            }
            else if (!node.Command.Method.IsFlowControl)
            {
                if (Recharge(node.Command) is { } recharge) seconds += recharge.Source.EstimatedSeconds * CombatFlowPolicy.Attempts(node.Command);
                if (actor != node.Command.Name) seconds += CombatFlowPolicy.SwitchSeconds;
                actor = node.Command.Name;
                seconds += CombatFlowPolicy.ActionSeconds(node.Command);
            }
        }
        return seconds;
    }

    private void CompileBlockBudgets()
    {
        var complete = new HashSet<CombatFlowBlock>();
        void CompileBlock(CombatFlowBlock block)
        {
            if (complete.Contains(block)) return;
            string? actor = null;
            void Require(IEnumerable<string> records) => block.EntryCoverageRecords.UnionWith(records.Where(record => !block.ProducedRecords.Contains(record)));
            foreach (var node in block.Nodes)
            {
                var unconditional = node.Command.RoundParity == null && node.Command.ActivatingRound.Count == 0 &&
                    (node.Condition == null || node.Condition.EvaluateBoolean((_, _) => null) == true);
                var target = node.Block ?? (node.Command.Method == Method.Call ? Blocks[node.Command.Args![0]] : null);
                if (target != null)
                {
                    CompileBlock(target);
                    block.EstimatedSeconds += target.EstimatedSeconds;
                    block.CoverageRecords.UnionWith(target.CoverageRecords);
                    Require(target.EntryCoverageRecords);
                    if (unconditional && node.Command.HasFlag("required")) block.ProducedRecords.UnionWith(target.ProducedRecords);
                    actor = null;
                }
                else if (node.Command.Method == Method.Branch)
                {
                    var constant = node.Condition!.EvaluateBoolean((_, _) => null);
                    var keys = constant == true ? new[] { "then" } : constant == false ? ["else"] : new[] { "then", "else", "unknown" };
                    var branches = keys.Select(key => node.Command.Options.TryGetValue(key, out var name) ? Blocks[name] : null).ToArray();
                    foreach (var branch in branches.Where(branch => branch != null)) CompileBlock(branch!);
                    block.EstimatedSeconds += branches.Max(branch => branch?.EstimatedSeconds ?? 0);
                    // 不把互斥分支的所有效果要求合并成必须同时成立；仅共同前提属于外层。
                    var common = new HashSet<string>(branches[0]?.CoverageRecords ?? [], StringComparer.Ordinal);
                    foreach (var branch in branches.Skip(1)) common.IntersectWith(branch?.CoverageRecords ?? []);
                    block.CoverageRecords.UnionWith(common);
                    var entry = new HashSet<string>(branches[0]?.EntryCoverageRecords ?? [], StringComparer.Ordinal);
                    var produced = new HashSet<string>(branches[0]?.ProducedRecords ?? [], StringComparer.Ordinal);
                    foreach (var branch in branches.Skip(1))
                    {
                        entry.IntersectWith(branch?.EntryCoverageRecords ?? []);
                        produced.IntersectWith(branch?.ProducedRecords ?? []);
                    }
                    Require(entry);
                    if (node.Command.HasFlag("required")) block.ProducedRecords.UnionWith(produced);
                    actor = null;
                }
                else if (!node.Command.Method.IsFlowControl)
                {
                    if (Recharge(node.Command) is { } recharge)
                    {
                        CompileBlock(recharge.Source);
                        block.EstimatedSeconds += recharge.Source.EstimatedSeconds * CombatFlowPolicy.Attempts(node.Command);
                        block.CoverageRecords.UnionWith(recharge.Source.CoverageRecords);
                        Require(recharge.Source.EntryCoverageRecords);
                    }
                    if (actor != node.Command.Name) block.EstimatedSeconds += CombatFlowPolicy.SwitchSeconds;
                    actor = node.Command.Name;
                    block.EstimatedSeconds += CombatFlowPolicy.ActionSeconds(node.Command);
                    if (node.Command.Options.TryGetValue("keep", out var record))
                    {
                        block.CoverageRecords.Add(record);
                        Require([record]);
                    }
                }
                if (unconditional && (node.Command.Method == Method.Record || node.Command.HasFlag("required")))
                {
                    var produced = node.Command.Method == Method.Record ? node.Command.Args![0] : node.Command.Options.GetValueOrDefault("record");
                    if (produced != null) block.ProducedRecords.Add(produced);
                }
            }
            if (block.CompletionRecord is { } completion) block.ProducedRecords.Add(completion);
            if (block.Atomic && block.EstimatedSeconds > block.Timeout)
                throw block.Error("atomic 片段超过有界执行预算，请拆分动作或显式调整 timeout：" + block.Name);
            complete.Add(block);
        }
        foreach (var block in Blocks.Values.Append(Root)) CompileBlock(block);
    }

    public CombatTiming? GetTiming(string actor, string action, bool hold = false) => _resolvedTimings
        .Where(pair => pair.Key.Name == actor && pair.Key.Method == Method.GetEnumByCode(action) &&
            (pair.Key.Args?.Contains("hold") == true) == hold)
        .Select(pair => pair.Value).Distinct().SingleOrDefault();

    private static ConditionEvaluator.CompiledCondition? CompileCondition(CombatCommand command, string key)
    {
        if (!command.Options.TryGetValue(key, out var expression)) return null;
        try { return ConditionEvaluator.Compile(expression); }
        catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException &&
            exception.Data["CombatSourceLocated"] is not true)
        { throw command.Error(exception.Message, exception); }
    }

    internal static double? Number(CombatCommand command, string key, bool positive = false)
    {
        if (!command.Options.TryGetValue(key, out var value)) return null;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
            !double.IsFinite(number) || (positive ? number <= 0 : number < 0))
            throw command.Error(key + " 必须是有效秒数");
        return number;
    }
}
