using System;
using System.Collections.Generic;
using System.Threading;
using BetterGenshinImpact.GameTask.AutoFight.Model;

namespace BetterGenshinImpact.GameTask.AutoFight.Script;

internal static class GatheredLootCommands
{
    // Execute against the picker's owning team, never the battle's stale team.
    // The delegate seam is the game-input boundary; tests do not operate a game.
    internal static bool Run(Avatar picker, IReadOnlyList<CombatCommand> commands,
        Action observe, Action releaseInputs, CancellationToken ct,
        Func<CombatCommand, CombatScenes, CombatCommand?, bool>? execute = null)
    {
        execute ??= static (command, scene, previous) => command.Execute(scene, previous);
        try
        {
            CombatCommand? previous = null;
            foreach (var command in commands)
            {
                ct.ThrowIfCancellationRequested();
                if (!execute(command, picker.CombatScenes, previous)) return false;
                previous = command;
                observe();
            }
            return commands.Count > 0;
        }
        finally { releaseInputs(); }
    }
}
