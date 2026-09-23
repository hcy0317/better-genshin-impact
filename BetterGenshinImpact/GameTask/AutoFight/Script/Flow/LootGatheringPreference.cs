using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Helpers;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

/// <summary>只识别旧采集模板的相邻替代块，不是全局角色过滤器。</summary>
internal static class LootGatheringPreference
{
    internal static IReadOnlyList<CombatCommand> Select(IReadOnlyList<CombatCommand> commands, ISet<string> party)
    {
        if (!party.Contains("枫原万叶") || !party.Contains("琴") || commands.Any(command => command.RequiresFlow)) return commands;
        var result = new List<CombatCommand>();
        var previousWasKazuhaGather = false;
        for (var start = 0; start < commands.Count;)
        {
            var end = start + 1;
            while (end < commands.Count && commands[end].Name == commands[start].Name) end++;
            var block = commands.Skip(start).Take(end - start).ToArray();
            var qinAlternative = block[0].Name == "琴" && previousWasKazuhaGather && IsGather(block, qin: true);
            if (!qinAlternative) result.AddRange(block);
            previousWasKazuhaGather = block[0].Name == "枫原万叶" && IsGather(block, qin: false);
            start = end;
        }
        return result;
    }

    private static bool IsGather(CombatCommand[] block, bool qin)
    {
        var down = Array.FindIndex(block, command => IsE(command, Method.KeyDown));
        var up = Array.FindIndex(block, command => IsE(command, Method.KeyUp));
        if (down < 0 || up <= down || block.Count(command => IsE(command, Method.KeyDown)) != 1 ||
            block.Count(command => IsE(command, Method.KeyUp)) != 1) return false;
        var held = new HashSet<User32.VK>();
        foreach (var command in block)
        {
            if (command.Method == Method.KeyDown || command.Method == Method.KeyUp)
            {
                var key = User32Helper.ToVk(command.Args![0]);
                if (key != User32.VK.VK_E && (qin || key is not (User32.VK.VK_W or User32.VK.VK_A or User32.VK.VK_S or User32.VK.VK_D))) return false;
                if (command.Method == Method.KeyDown) { if (!held.Add(key)) return false; }
                else if (!held.Remove(key)) return false;
            }
            else if (command.Method != Method.Attack && command.Method != Method.Wait &&
                !(qin && command.Method == Method.MoveBy) &&
                !(qin && command.Method == Method.Click && command.Args is { Count: 1 } && command.Args[0].Equals("middle", StringComparison.OrdinalIgnoreCase)) &&
                !(!qin && (command.Method == Method.W || command.Method == Method.A || command.Method == Method.S || command.Method == Method.D))) return false;
        }
        if (held.Count != 0) return false;
        if (!qin) return block.Any(command => command.Method == Method.Attack);
        return block.Skip(down + 1).Take(up - down - 1).Count(command => command.Method == Method.MoveBy) >= 3 &&
            block.Skip(up + 1).Any(command => command.Method == Method.Click);
    }

    private static bool IsE(CombatCommand command, Method method) => command.Method == method &&
        command.Args is { Count: 1 } && User32Helper.ToVk(command.Args[0]) == User32.VK.VK_E;
}
