using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>仅识别单角色 E down/等待或瞄准/up；不改变 legacy atomic 的预算。</summary>
internal static class CombatHeldEPlan
{
    internal static bool Matches(CombatFlowBlock block)
    {
        if (!block.Atomic || block.Requires != null || block.Nodes.Count < 3) return false;
        var first = block.Nodes[0].Command;
        var last = block.Nodes[^1].Command;
        static bool E(CombatCommand command) => command.Args?.FirstOrDefault() is { } key &&
            BetterGenshinImpact.Helpers.User32Helper.ToVk(key) == Vanara.PInvoke.User32.VK.VK_E;
        return first.Method == Method.KeyDown && last.Method == Method.KeyUp && E(first) && E(last) &&
            first.Name != CombatScriptParser.CurrentAvatarName &&
            block.Nodes.All(node => node.Block == null && node.Command.Name == first.Name &&
                node.Condition == null && node.Command.RoundParity == null && node.Command.ActivatingRound.Count == 0) &&
            block.Nodes.Skip(1).Take(block.Nodes.Count - 2).All(node =>
                node.Command.Method == Method.Wait || node.Command.Method == Method.MoveBy);
    }
}
