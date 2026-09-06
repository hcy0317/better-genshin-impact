using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Config;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>原 JSON 优先级政策的薄宿主；动作、片段和本场状态都由同一流程核心执行。</summary>
public sealed class JsonCombatFlowExecution : IDisposable
{
    private sealed record Root(JsonAction Action, CombatFlowExecution Execution);
    private sealed record Entry(Root Root, ConditionEvaluator.CompiledCondition Condition, int Priority);
    private sealed record PreparedEntry(int RootIndex, ConditionEvaluator.CompiledCondition Condition, int Priority, string Source);
    private readonly CombatFlowBattleState _battle;
    private readonly ICombatFlowGame _game;
    private readonly Root[] _roots;
    private readonly Entry[] _entries;
    private Root? _active;
    private readonly HashSet<Root> _unproductiveRoots = [];
    private bool _closed;
    public CombatFlowContext Context => _battle.Context;
    public CombatFlowStatistics RuntimeStatistics => _battle.Diagnostics.Snapshot();
    public IReadOnlyCollection<string> Actors { get; }
    public IReadOnlyList<string> Diagnostics { get; }
    public bool IsAtomic => _active?.Execution.IsAtomic == true;
    public bool IsAtRootBoundary => _active?.Execution.IsAtRootBoundary != false;
    public bool TakeFinishCheckRequest() => _battle.TakeFinishCheckRequest();

    public static bool RequiresFlow(JsonCombatStrategy strategy) => strategy.Info.Declarations.Count != 0 ||
        strategy.Actions.Any(action => CombatScriptParser.ParseLineCommands(action.Action,
            string.IsNullOrWhiteSpace(action.Character) ? CombatScriptParser.CurrentAvatarName : action.Character)
            .Any(command => command.RequiresFlow));

    public JsonCombatFlowExecution(JsonCombatStrategy strategy, ICombatFlowGame game,
        SkillCatalogSnapshot? database = null, TimeProvider? clock = null)
    {
        _game = game;
        var declarations = CombatScriptParser.ParseContext(string.Join("\n", strategy.Info.Declarations));
        var commands = declarations.CombatCommands.ToList();
        foreach (var command in commands) command.SourceFile = "JSON.info.declarations";
        var actors = declarations.AvatarNames.ToHashSet(StringComparer.Ordinal);
        var roots = new List<(JsonAction Action, string Name, bool Loop)>();
        for (var i = 0; i < strategy.Actions.Count; i++)
        {
            var action = strategy.Actions[i];
            var actor = string.IsNullOrWhiteSpace(action.Character) ? CombatScriptParser.CurrentAvatarName
                : DefaultAutoFightConfig.AvatarAliasToStandardName(action.Character);
            var body = CombatScriptParser.ParseLineCommands(action.Action, actor);
            var loops = body.FirstOrDefault()?.Method == Method.Strategy;
            if (loops)
            {
                if (body[0].Options.GetValueOrDefault("loop") != "battle") throw body[0].Error("仅支持 strategy(loop=battle)");
                body.RemoveAt(0);
            }
            if (body.Any(command => command.Method == Method.Timing || command.Method == Method.Strategy))
                throw new FormatException("JSON timing 应放入 info.declarations；strategy(loop=battle) 仅放在根 action 开头");
            foreach (var command in body)
            {
                command.SourceFile = $"JSON.actions[{i}].action";
                if (!command.Method.IsFlowControl && command.Name != CombatScriptParser.CurrentAvatarName) actors.Add(command.Name);
                if (action.EnsureCast && command.Method == Method.Skill) command.Flags.Add("required");
            }
            var rootName = "$json-root:" + i;
            commands.Add(new("", $"segment(start,name={rootName},define)") { SourceFile = $"JSON.actions[{i}].action", IsCompilerGenerated = true });
            commands.AddRange(body);
            commands.Add(new("", "segment(end)") { SourceFile = $"JSON.actions[{i}].action", IsCompilerGenerated = true });
            roots.Add((action, rootName, loops));
        }
        var program = CombatFlowProgram.Compile(new CombatScript(actors, commands), database);
        if (program.Root.Nodes.Count != 0) throw new FormatException("JSON info.declarations 只能包含 timing 与 define 片段");
        var names = strategy.Actions.Select(action => action.Name).ToArray();
        var prepared = new List<PreparedEntry>();
        for (var i = 0; i < roots.Count; i++)
        {
            var action = roots[i].Action;
            PreparedEntry Prepare(string expression, int priority, string source)
            {
                try
                {
                    var condition = ConditionEvaluator.Compile(expression, names);
                    var rootBlock = program.Blocks[roots[i].Name];
                    CombatFlowValidation.ValidateConditionReferences(program, rootBlock, condition, rootBlock.Declaration!);
                    return new(i, condition, priority, source);
                }
                catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException)
                { throw CombatCommand.SyntaxError(exception.Message, 1, 1, exception, source); }
            }
            prepared.Add(Prepare(action.Condition.Expression, action.Index, $"JSON.actions[{i}].condition.expression"));
            prepared.AddRange(action.MorePriorities.Select((priority, index) =>
                Prepare(priority.Expression, priority.Priority, $"JSON.actions[{i}].morePriorities[{index}].expression")));
        }
        for (var i = 0; i < roots.Count; i++)
        {
            if (roots[i].Loop) continue;
            const string reason = "JSON round/round-odd 需要本根显式 strategy(loop=battle)；非循环根请使用 call-index";
            foreach (var entry in prepared.Where(entry => entry.RootIndex == i && entry.Condition.UsesFunction("round-odd")))
                throw CombatCommand.SyntaxError(reason, 1, 1, file: entry.Source);
            foreach (var block in program.ReachableBlocks(program.Blocks[roots[i].Name]))
            {
                if (block.Requires?.UsesFunction("round-odd") == true) throw block.Error(reason);
                foreach (var node in block.Nodes)
                    if (node.Command.RoundParity != null || node.Command.ActivatingRound.Count != 0 || node.Condition?.UsesFunction("round-odd") == true)
                        throw node.Command.Error(reason);
            }
        }
        ValidateOpenings(program, roots, prepared);
        Actors = program.Actors;
        Diagnostics = program.Diagnostics;
        _battle = new(clock);
        _roots = roots.Select(root => new Root(root.Action, new(program, game, _battle, program.Blocks[root.Name],
            root.Loop, root.Loop, yieldAtRootBoundaries: true, jsonAction: root.Action))).ToArray();
        _battle.History.KnownNames.UnionWith(names);
        _entries = prepared.Select(entry => new Entry(_roots[entry.RootIndex], entry.Condition, entry.Priority))
            .OrderBy(entry => entry.Priority).ToArray();
    }

    public async ValueTask<CombatFlowStep> StepAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        ct.ThrowIfCancellationRequested();
        _game.BeginStep();
        if (_active == null || _active.Execution.IsAtRootBoundary && !_active.Execution.NeedsCompletion)
        {
            Root? Select() => _entries.FirstOrDefault(entry => !_unproductiveRoots.Contains(entry.Root) &&
                entry.Root.Execution.EvaluateCondition(entry.Condition, entry.Root.Action.Character) == true)?.Root;
            _active = Select();
            if (_active == null && _unproductiveRoots.Count != 0)
            {
                // 其他可执行根均已获得机会；前次核心的空闲让出已完成，不再叠加固定 sleep。
                _unproductiveRoots.Clear();
                _active = Select();
            }
        }
        if (_active == null)
        {
            await _game.YieldAsync(ct);
            return new(CombatFlowResult.Skipped, false);
        }
        var step = await _active.Execution.StepAsync(ct, beginObservationFrame: false);
        if (_active.Execution.MadeProgressInRound) _unproductiveRoots.Clear();
        if (step.RoundCompleted)
        {
            if (step.Result == CombatFlowResult.Succeeded && _active.Execution.LastRoundHadAction)
                _battle.History.Record(_active.Action.Index, _active.Action.Name, Context.Now);
            if (!_active.Execution.MadeProgressInRound) _unproductiveRoots.Add(_active);
            _active = null;
        }
        return step;
    }

    public void Dispose()
    {
        if (_closed) return;
        _closed = true;
        _battle.Dispose();
        foreach (var root in _roots) root.Execution.Dispose();
        _active = null;
        _unproductiveRoots.Clear();
    }

    private static void ValidateOpenings(CombatFlowProgram program, List<(JsonAction Action, string Name, bool Loop)> roots,
        List<PreparedEntry> entries)
    {
        static bool IsOpening(CombatCommand? command) => command?.Method == Method.Call &&
            command.HasFlag("required") && command.Options.GetValueOrDefault("once") == "battle";
        var openingNames = roots.Select(root => program.Blocks[root.Name].Nodes.FirstOrDefault()?.Command)
            .Where(IsOpening).Select(command => command!.Args![0]).Distinct(StringComparer.Ordinal);
        foreach (var opening in openingNames)
        {
            var openingBlock = program.Blocks[opening];
            var completion = openingBlock.CompletionRecord;
            bool IsCompletion(string? name) => completion != null && name != null &&
                CombatFlowContext.NormalizeName(name) == CombatFlowContext.NormalizeName(completion);
            bool BlocksWhenMissing(ConditionEvaluator.CompiledCondition? condition) => completion != null && condition != null &&
                condition.EvaluateBoolean((function, args) => function == "record-exists" &&
                    IsCompletion(args.FirstOrDefault()?.ToString()) ? false : null) == false;
            void ValidateCompletionProducer()
            {
                if (completion == null) return;
                foreach (var block in program.Blocks.Values.Append(program.Root))
                {
                    if (block != openingBlock && IsCompletion(block.CompletionRecord))
                        throw block.Completion!.Error("跨根开场完成记录只能由对应开场的结束位置生成：" + completion);
                    foreach (var node in block.Nodes)
                        if (IsCompletion(node.Command.Options.GetValueOrDefault("record")) ||
                            node.Command.Method == Method.Record && IsCompletion(node.Command.Args![0]))
                            throw node.Command.Error("跨根开场完成记录不能由动作或提前标记伪造：" + completion);
                }
            }
            for (var rootIndex = 0; rootIndex < roots.Count; rootIndex++)
            {
                var root = roots[rootIndex];
                var nodes = program.Blocks[root.Name].Nodes;
                var first = nodes.FirstOrDefault()?.Command;
                if (IsOpening(first) && first!.Args![0] == opening) continue;
                var guardedBody = nodes.Count != 0 && nodes.All(node => BlocksWhenMissing(node.Condition) ||
                    BlocksWhenMissing(node.Block?.Requires) || node.Command.Method == Method.Call &&
                    BlocksWhenMissing(program.Blocks[node.Command.Args![0]].Requires));
                if (!guardedBody && !entries.Where(entry => entry.RootIndex == rootIndex).All(entry => BlocksWhenMissing(entry.Condition)))
                    throw program.Blocks[root.Name].Error($"JSON 根 {root.Action.Name} 可能绕过 required 开场 {opening}；请共享开场调用，或以 record-exists({completion ?? "开场完成记录"}) 作为必需前提");
                if (entries.Where(entry => entry.RootIndex == rootIndex).Select(entry => entry.Condition)
                        .Concat(nodes.Select(node => node.Condition)).Concat(nodes.Select(node => node.Block?.Requires))
                        .Concat(nodes.Where(node => node.Command.Method == Method.Call).Select(node => program.Blocks[node.Command.Args![0]].Requires))
                        .Any(condition => condition?.References.Any(reference => IsCompletion(reference.Name)) == true))
                    ValidateCompletionProducer();
            }
        }
    }
}
