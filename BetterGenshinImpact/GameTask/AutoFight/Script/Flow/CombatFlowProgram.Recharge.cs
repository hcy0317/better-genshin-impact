using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoFight.Config;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal sealed record CombatRechargePlan(CombatFlowBlock Source, IReadOnlyList<CombatFlowNode> Producers);

public sealed partial class CombatFlowProgram
{
    private readonly Dictionary<CombatCommand, CombatRechargePlan> _recharges = new();
    internal CombatRechargePlan? Recharge(CombatCommand command) => _recharges.GetValueOrDefault(command);

    private void CompileRecharge()
    {
        var nodes = Blocks.Values.Append(Root).SelectMany(block => block.Nodes).ToArray();
        foreach (var node in nodes.Where(node => node.Command.HasFlag("recharge")))
        {
            var command = node.Command;
            var from = command.Options["from"];
            CombatFlowBlock source;
            CombatFlowNode[] producers;
            if (from.StartsWith("segment:", StringComparison.Ordinal))
            {
                var name = from[8..];
                if (!Blocks.TryGetValue(name, out source!)) throw command.Error("未声明供能片段：" + name);
                producers = Descendants(source).Where(candidate => Feed(candidate.Command)?.Name == command.Name).ToArray();
            }
            else
            {
                var actor = DefaultAutoFightConfig.AvatarAliasToStandardName(from);
                producers = nodes.Where(candidate => candidate.Command.Method == Method.Skill && candidate.Command.Name == actor &&
                    Feed(candidate.Command)?.Name == command.Name).ToArray();
                if (producers.Length != 1) throw command.Error("from 角色必须唯一对应一个获准的 feed 生成点；请改用 from=segment:片段名：" + from);
                source = new("$feed:" + actor + ":" + Array.IndexOf(nodes, node)) { Declaration = command };
                source.Nodes.Add(producers[0]);
            }
            if (producers.Length == 0 || producers.All(producer => producer.Condition?.EvaluateBoolean((_, _) => null) == false))
                throw command.Error("供能来源缺少可执行的同目标 feed 生成点：" + from);
            _recharges.Add(command, new(source, producers));
        }

        CombatFlowValidation.ValidateCallGraph(this);
    }

    private IEnumerable<CombatFlowNode> Descendants(CombatFlowBlock block)
    {
        foreach (var node in block.Nodes)
        {
            yield return node;
            var child = node.Block ?? (node.Command.Method == Method.Call ? Blocks[node.Command.Args![0]] : null);
            if (child != null)
                foreach (var nested in Descendants(child)) yield return nested;
            if (node.Command.Method == Method.Branch)
                foreach (var key in new[] { "then", "else", "unknown" })
                    if (node.Command.Options.TryGetValue(key, out var target))
                        foreach (var nested in Descendants(Blocks[target])) yield return nested;
        }
    }

    internal IEnumerable<CombatFlowBlock> ReachableBlocks(CombatFlowBlock entry)
    {
        var visited = new HashSet<CombatFlowBlock>();
        var pending = new Stack<CombatFlowBlock>();
        pending.Push(entry);
        while (pending.TryPop(out var block))
        {
            if (!visited.Add(block)) continue;
            yield return block;
            if (block.OnFail is { } recovery) pending.Push(Blocks[recovery]);
            foreach (var node in block.Nodes)
            {
                if (node.Block != null) pending.Push(node.Block);
                if (node.Command.Method == Method.Call || node.Command.Method == Method.JumpTo) pending.Push(Blocks[node.Command.Args![0]]);
                if (node.Command.Method == Method.Branch)
                    foreach (var key in new[] { "then", "else", "unknown" })
                        if (node.Command.Options.TryGetValue(key, out var target)) pending.Push(Blocks[target]);
                if (node.Command.Options.TryGetValue("watch-target", out var maintenance)) pending.Push(Blocks[maintenance]);
                if (Recharge(node.Command) is { } recharge) pending.Push(recharge.Source);
            }
        }
    }
}
