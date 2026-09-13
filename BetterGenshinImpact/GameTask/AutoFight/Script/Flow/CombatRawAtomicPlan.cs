using System;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>只对单角色、纯记录条件的鼠标原语/等待序列采用同步观察重叠预算。</summary>
internal sealed class CombatRawAtomicPlan(CombatFlowBlock block)
{
    private const double DecisionSeconds = 0.15;
    internal static CombatRawAtomicPlan? Create(CombatFlowBlock block)
    {
        bool Pure(ConditionEvaluator.CompiledCondition? condition) => condition == null ||
            !ConditionEvaluator.FunctionNames.Any(function => condition.UsesFunction(function) &&
                function is not ("record-active" or "record-remaining" or "record-age" or "record-exists"));
        if (!block.Atomic || block.Nodes.Count == 0 || !Pure(block.Requires)) return null;
        string? actor = null;
        foreach (var node in block.Nodes)
        {
            var command = node.Command;
            if (node.Block != null || !Pure(node.Condition) || command.RoundParity != null || command.ActivatingRound.Count != 0 ||
                command.Flags.Any(flag => flag != "required") ||
                command.Options.Keys.Any(key => key is not ("keep" or "if"))) return null;
            if (command.Method == Method.Record) continue;
            if (command.Name == CombatScriptParser.CurrentAvatarName || actor != null && actor != command.Name) return null;
            actor = command.Name;
            var mouseKey = (command.Method == Method.KeyDown || command.Method == Method.KeyUp) &&
                command.Args?.FirstOrDefault() is "VK_LBUTTON" or "VK_RBUTTON" or "VK_MBUTTON";
            if (!mouseKey && command.Method != Method.MoveBy && command.Method != Method.Wait) return null;
        }
        return actor == null ? null : new(block);
    }

    internal double Estimate(int index)
    {
        var seconds = index == 0 ? CombatFlowPolicy.SwitchSeconds : 0;
        var prepared = index > 0 && block.Nodes[index - 1].Command.Method == Method.Wait;
        foreach (var node in block.Nodes.Skip(index))
        {
            var command = node.Command;
            if (command.Method == Method.Record) continue;
            if (command.Method == Method.Wait)
            {
                seconds += Math.Max(CombatFlowPolicy.ActionSeconds(command), DecisionSeconds);
                prepared = true;
            }
            else
            {
                if (!prepared) seconds += DecisionSeconds;
                prepared = false;
            }
        }
        return seconds;
    }
}
