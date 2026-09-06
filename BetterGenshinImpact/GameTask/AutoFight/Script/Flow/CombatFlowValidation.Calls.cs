using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

internal static partial class CombatFlowValidation
{
    private sealed record CallEdge(CombatFlowBlock Target, bool GrowsStack, CombatCommand? Source);

    internal static void ValidateCallGraph(CombatFlowProgram program)
    {
        var graph = new Dictionary<CombatFlowBlock, List<CallEdge>>();
        var pending = new Queue<CombatFlowBlock>(program.Blocks.Values.Append(program.Root));
        while (pending.TryDequeue(out var block))
        {
            if (graph.ContainsKey(block)) continue;
            var edges = new List<CallEdge>();
            graph.Add(block, edges);
            void Add(CombatFlowBlock target, bool grows, CombatCommand? source)
            { edges.Add(new(target, grows, source)); pending.Enqueue(target); }
            void Named(string name, bool grows, CombatCommand? source)
            {
                if (!program.Blocks.TryGetValue(name, out var target))
                    throw source?.Error("未声明片段：" + name) ?? block.Error("未声明片段：" + name);
                Add(target, grows, source);
            }
            if (block.OnFail is { } recovery) Named(recovery, true, block.Declaration);
            foreach (var node in block.Nodes)
            {
                var command = node.Command;
                if (node.Block != null) Add(node.Block, true, command);
                if (command.Method == Method.Call || command.Method == Method.JumpTo)
                    Named(command.Args![0], command.Method == Method.Call, command);
                if (command.Method == Method.Branch)
                    foreach (var key in new[] { "then", "else", "unknown" })
                        if (command.Options.TryGetValue(key, out var branch)) Named(branch, true, command);
                if (command.Options.TryGetValue("watch-target", out var maintenance)) Named(maintenance, true, command);
                if (program.Recharge(command) is { } recharge) Add(recharge.Source, true, command);
            }
        }

        // 强连通分量内只允许纯尾转移环；任何压栈边参与的环都是递归，不能靠 jump 隐藏。
        var indices = new Dictionary<CombatFlowBlock, int>();
        var lowest = new Dictionary<CombatFlowBlock, int>();
        var active = new HashSet<CombatFlowBlock>();
        var stack = new Stack<CombatFlowBlock>();
        var sequence = 0;
        void Visit(CombatFlowBlock block)
        {
            indices[block] = lowest[block] = sequence++;
            stack.Push(block);
            active.Add(block);
            foreach (var edge in graph[block])
            {
                if (!indices.ContainsKey(edge.Target))
                {
                    Visit(edge.Target);
                    lowest[block] = Math.Min(lowest[block], lowest[edge.Target]);
                }
                else if (active.Contains(edge.Target)) lowest[block] = Math.Min(lowest[block], indices[edge.Target]);
            }
            if (lowest[block] != indices[block]) return;
            var component = new HashSet<CombatFlowBlock>();
            CombatFlowBlock member;
            do { member = stack.Pop(); active.Remove(member); component.Add(member); } while (member != block);
            var growing = component.SelectMany(node => graph[node]).FirstOrDefault(edge => edge.GrowsStack && component.Contains(edge.Target));
            if (growing != null) throw growing.Source?.Error("片段存在递归调用环（含调用/分支/补能与尾转移组合）")
                ?? block.Error("片段存在递归调用环");
        }
        foreach (var block in graph.Keys) if (!indices.ContainsKey(block)) Visit(block);
    }
}
