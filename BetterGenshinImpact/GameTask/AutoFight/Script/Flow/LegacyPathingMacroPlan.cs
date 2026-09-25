using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Helpers;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>旧路径宏的顺序边界。先保护持键跨度，再区分物理段与完整角色段。</summary>
internal sealed record LegacyPathingMacroPlan(IReadOnlyList<LegacyPathingMacroPlan.Segment> Segments, double BudgetSeconds)
{
    internal sealed record Segment(bool IsRaw, IReadOnlyList<CombatCommand> Commands, bool CannonProgram = false)
    {
        internal double RawBudgetSeconds => Math.Max(CombatFlowPolicy.AtomicSeconds,
            Commands.Sum(CombatFlowPolicy.ActionSeconds) + CombatFlowPolicy.SwitchSeconds +
            Commands.Count * .15 + CombatFlowPolicy.RecoverySeconds);
    }

    internal static LegacyPathingMacroPlan Create(CombatScript script, IEnumerable<string> party)
    {
        if (script.HasFlowCommands) throw new InvalidOperationException("增强策略不能分段为旧路径宏");
        var available = LegacyCombatFlowAdapter.ValidateParty(script.CombatCommands, party,
            CombatScriptExecutionMode.LegacyPartyTemplate);
        var preferred = LootGatheringPreference.Select(script.CombatCommands, available);
        var commands = preferred.Where(command => command.Name == CombatScriptParser.CurrentAvatarName ||
            available.Contains(command.Name)).ToArray();
        var segments = new List<Segment>();
        // 实际炮台程序在同一匿名序列中反复交互/退出。只把这一受限程序保留为整体，
        // 不能全局把普通RETURN/ESCAPE从已有原生UI准入改成无条件物理键。
        var cannonProgram = commands.All(IsCannonCommand) &&
            commands.Any(command => command.Method == Method.KeyPress && User32Helper.ToVk(command.Args![0]) == User32.VK.VK_F) &&
            commands.Any(command => command.Method == Method.KeyPress && User32Helper.ToVk(command.Args![0]) == User32.VK.VK_ESCAPE) &&
            (commands.Any(command => command.Method == Method.KeyPress && User32Helper.ToVk(command.Args![0]) == User32.VK.VK_RETURN) ||
                commands.Count(command => command.Method == Method.KeyPress && User32Helper.ToVk(command.Args![0]) == User32.VK.VK_ESCAPE) > 1);
        if (cannonProgram) segments.Add(new(true, commands, true));
        for (var index = 0; !cannonProgram && index < commands.Length;)
        {
            var end = index;
            if (commands[index].Method == Method.KeyDown)
            {
                var held = new HashSet<User32.VK>();
                for (; end < commands.Length; end++)
                {
                    var command = commands[end];
                    if (command.Method == Method.KeyDown) held.Add(User32Helper.ToVk(command.Args![0]));
                    if (command.Method == Method.KeyUp) held.Remove(User32Helper.ToVk(command.Args![0]));
                    if (held.Count == 0) break;
                }
                end = Math.Min(end, commands.Length - 1);
            }
            var span = commands[index..(end + 1)];
            var raw = span.All(IsRaw);
            if (segments.Count > 0 && segments[^1].IsRaw == raw)
                segments[^1] = new(raw, segments[^1].Commands.Concat(span).ToArray());
            else segments.Add(new(raw, span));
            index = end + 1;
        }
        return new(segments, Math.Max(CombatFlowPolicy.AtomicSeconds,
            commands.Sum(command => CombatFlowPolicy.ActionTimeout(command, null) +
                CombatFlowPolicy.SwitchSeconds + CombatFlowPolicy.RecoverySeconds)));
    }

    internal static bool IsCannonCommand(CombatCommand command)
    {
        if (command.Name != CombatScriptParser.CurrentAvatarName || command.RequiresFlow) return false;
        if (command.Method == Method.Wait || command.Method == Method.W || command.Method == Method.A ||
            command.Method == Method.S || command.Method == Method.D) return true;
        return command.Method == Method.KeyPress && command.Args is { Count: 1 } &&
            User32Helper.ToVk(command.Args[0]) is User32.VK.VK_W or User32.VK.VK_A or User32.VK.VK_S or
                User32.VK.VK_D or User32.VK.VK_F or User32.VK.VK_RETURN or User32.VK.VK_ESCAPE;
    }

    internal static bool IsRaw(CombatCommand command)
    {
        if (command.Name != CombatScriptParser.CurrentAvatarName || command.RequiresFlow) return false;
        if (command.Method == Method.Wait || command.Method == Method.Jump || command.Method == Method.MoveBy ||
            command.Method == Method.W || command.Method == Method.A || command.Method == Method.S || command.Method == Method.D)
            return true;
        if (command.Method == Method.Click)
            return command.Args is { Count: 1 } && string.Equals(command.Args[0], "middle", StringComparison.OrdinalIgnoreCase);
        if (command.Method == Method.MouseDown || command.Method == Method.MouseUp)
            return command.Args is null or { Count: 0 } || command.Args is { Count: 1 } &&
                string.Equals(command.Args[0], "left", StringComparison.OrdinalIgnoreCase);
        if (command.Method != Method.KeyDown && command.Method != Method.KeyUp && command.Method != Method.KeyPress)
            return false;
        return command.Args is { Count: 1 } && User32Helper.ToVk(command.Args[0]) is
            User32.VK.VK_W or User32.VK.VK_A or User32.VK.VK_S or User32.VK.VK_D or User32.VK.VK_SPACE or
            User32.VK.VK_E or User32.VK.VK_Q or User32.VK.VK_T or User32.VK.VK_F or User32.VK.VK_X;
    }
}
