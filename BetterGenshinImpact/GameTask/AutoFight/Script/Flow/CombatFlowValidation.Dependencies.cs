using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal static partial class CombatFlowValidation
{
    private sealed record RecordProducer(string Name, ConditionEvaluator.CompiledCondition? Condition,
        ConditionEvaluator.CompiledCondition? Requires, string? Keep, CombatCommand Source)
    {
        public IEnumerable<string> Dependencies => (Condition?.References ?? [])
            .Concat(Requires?.References ?? []).Select(reference => CombatFlowContext.NormalizeName(reference.Name))
            .Concat(Keep == null ? [] : new[] { CombatFlowContext.NormalizeName(Keep) });
    }

    private static void ValidateRecordDependencies(CombatFlowProgram program)
    {
        List<RecordProducer> producers = [];
        foreach (var block in program.Blocks.Values.Append(program.Root))
        {
            foreach (var node in block.Nodes)
            {
                var name = node.Command.Method == Method.Record ? node.Command.Args![0] : node.Command.Options.GetValueOrDefault("record");
                if (name != null) producers.Add(new(CombatFlowContext.NormalizeName(name), node.Condition,
                    block.Requires, node.Command.Options.GetValueOrDefault("keep"), node.Command));
            }
            if (block.CompletionRecord is { } completion)
                producers.Add(new(CombatFlowContext.NormalizeName(completion), null, block.Requires, null, block.Completion!));
        }

        // 仅拒绝能证明无法初始化的环。外部观测不确定时保留潜在可达性，不误禁合法主轴。
        var possible = new HashSet<string>(StringComparer.Ordinal);
        object? Resolve(string function, IReadOnlyList<object?> args) => function is "record-exists" or "record-active"
            ? possible.Contains(CombatFlowContext.NormalizeName(args[0]!.ToString()!)) : null;
        bool changed;
        do
        {
            changed = false;
            foreach (var producer in producers)
                if ((producer.Keep == null || possible.Contains(CombatFlowContext.NormalizeName(producer.Keep))) &&
                    producer.Condition?.EvaluateBoolean(Resolve) != false && producer.Requires?.EvaluateBoolean(Resolve) != false)
                    changed |= possible.Add(producer.Name);
        } while (changed);

        var blocked = producers.Where(producer => !possible.Contains(producer.Name)).GroupBy(producer => producer.Name)
            .ToDictionary(group => group.Key, group => group.SelectMany(producer => producer.Dependencies)
                .Where(name => !possible.Contains(name)).Distinct().ToArray(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        void Visit(string name)
        {
            if (visited.Contains(name) || !blocked.TryGetValue(name, out var dependencies)) return;
            if (!visiting.Add(name)) throw producers.First(producer => producer.Name == name).Source.Error("记录依赖环没有可达初始化来源：" + name);
            foreach (var dependency in dependencies) Visit(dependency);
            visiting.Remove(name);
            visited.Add(name);
        }
        foreach (var name in blocked.Keys) Visit(name);
    }

    private static void ValidateOpeningWatchTargets(CombatFlowProgram program)
    {
        HashSet<CombatFlowBlock> Reach(IEnumerable<CombatFlowBlock> entries, bool includeOnce)
        {
            var reached = new HashSet<CombatFlowBlock>();
            var pending = new Stack<CombatFlowBlock>(entries);
            while (pending.TryPop(out var block))
            {
                if (!reached.Add(block)) continue;
                foreach (var node in block.Nodes)
                {
                    if (node.Condition?.EvaluateBoolean((_, _) => null) == false) continue;
                    if (node.Block != null) pending.Push(node.Block);
                    if ((node.Command.Method == Method.Call || node.Command.Method == Method.JumpTo) &&
                        (includeOnce || node.Command.Options.GetValueOrDefault("once") != "battle"))
                        pending.Push(program.Blocks[node.Command.Args![0]]);
                    if (node.Command.Method == Method.Branch)
                        foreach (var key in new[] { "then", "else", "unknown" })
                            if (node.Command.Options.TryGetValue(key, out var target)) pending.Push(program.Blocks[target]);
                }
            }
            return reached;
        }
        var all = program.Blocks.Values.Append(program.Root).ToArray();
        var onceEntries = all.SelectMany(block => block.Nodes)
            .Where(node => node.Command.Method == Method.Call && node.Command.Options.GetValueOrDefault("once") == "battle")
            .Select(node => program.Blocks[node.Command.Args![0]]);
        var onceBlocks = Reach(onceEntries, includeOnce: true);
        var repeatBlocks = Reach(all.Where(block => block == program.Root || block.Declaration?.IsCompilerGenerated == true), includeOnce: false);
        foreach (var command in onceBlocks.SelectMany(block => block.Nodes).Select(node => node.Command))
            if (command.Options.TryGetValue("watch", out var name) && command.Options.GetValueOrDefault("watch-mode") != "call" &&
                !repeatBlocks.Any(block => block.WatchPoints.ContainsKey(name)))
                throw command.Error("once 开场不能成为唯一循环 watch 维护目标；请声明可重复维护点：" + name);
    }
}
