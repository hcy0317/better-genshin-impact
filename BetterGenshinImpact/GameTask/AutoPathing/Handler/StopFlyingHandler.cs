using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

public class StopFlyingHandler : IActionHandler
{
    public async Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
    {
        // 如果有参数，先自由落体，然后恢复飞行
        if (waypointForTrack != null
            && !string.IsNullOrEmpty(waypointForTrack.ActionParams)
            && int.TryParse(waypointForTrack.ActionParams, out var stopFlyingWaitTime))
        {
            Simulation.SendInput.SimulateAction(GIActions.Jump);
            await Delay(stopFlyingWaitTime, ct);
            Simulation.SendInput.SimulateAction(GIActions.Jump);
            await Delay(300, ct);
        }

        // 路线节点不等于当前仍在空中，已在地面时禁止把下落攻击变成普攻。
        var attacked = LandingAttackGuard.TryAttack(() =>
        {
            using var screen = CaptureToRectArea();
            return Bv.GetMotionStatus(screen);
        }, () => Simulation.SendInput.SimulateAction(GIActions.NormalAttack), ct);
        if (!attacked)
        {
            Logger.LogInformation("动作：未确认飞行，跳过下落攻击");
            return;
        }
        Logger.LogInformation("动作：下落攻击");
        int i;
        for (i = 0; i < 50; i++)
        {
            using var screen = CaptureToRectArea();
            var isFlying = Bv.GetMotionStatus(screen) == MotionStatus.Fly;
            if (isFlying)
            {
                await Delay(300, ct);
            }
            else
            {
                break;
            }
        }

        if (i == 50)
        {
            Logger.LogWarning("动作：下落攻击 超时结束");
        }
        else
        {
            Logger.LogInformation("动作：下落攻击 结束");
        }
    }
}
