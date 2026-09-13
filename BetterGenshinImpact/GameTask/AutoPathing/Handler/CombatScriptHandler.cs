using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

public class CombatScriptHandler : IActionHandler
{
    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
    {
        if (waypointForTrack is { CombatScript: not null })
        {
            Logger.LogInformation("执行 {Text}", "简易策略脚本");
            var combatScript = waypointForTrack.CombatScript;
            var combatScenes = await RunnerContext.Instance.GetCombatScenes(ct);
            if (combatScenes == null)
            {
                throw new InvalidOperationException("队伍识别未初始化成功，不能将简易策略标为完成");
            }

            combatScenes.BeforeTask(ct);
            var result = await CombatScriptExecutor.ExecuteAsync(combatScript, ct, Logger, combatScenes,
                CombatScriptExecutionMode.LegacyPartyTemplate);
            EnsureFragmentCompleted(result);
            Logger.LogDebug("简易策略结果 {Kind}：{Reason}", result.Kind, result.Reason);
        }
        else
        {
            throw new InvalidOperationException("策略脚本action_params内容为空");
        }
    }

    internal static void EnsureFragmentCompleted(CombatExecutionResult result) => result.EnsureCanContinue();

    // private static bool IsOnlyCurrentAvatar(CombatScript combatScript)
    // {
    //     var isCurrentAvatar = false;
    //     if (combatScript.AvatarNames.Count == 1)
    //     {
    //         foreach (var avatarName in combatScript.AvatarNames)
    //         {
    //             if (avatarName == CombatScriptParser.CurrentAvatarName)
    //             {
    //                 isCurrentAvatar = true;
    //                 break;
    //             }
    //         }
    //     }
    //
    //     return isCurrentAvatar;
    // }
}
