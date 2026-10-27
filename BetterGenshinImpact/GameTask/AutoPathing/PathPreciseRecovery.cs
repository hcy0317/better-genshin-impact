using System;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator.Extensions;

namespace BetterGenshinImpact.GameTask.AutoPathing;

// Near a waypoint, the long backward/jump macro can move farther away than the
// remaining 60ms approach pulses can cover. Probe sideways under the same scope;
// displacement only ends this probe, never confirms arrival or replenishes steps.
internal static class PathPreciseRecovery
{
    internal static async Task<bool> SidestepAsync(PathRecoveryScope scope, PathMoveObservation start)
    {
        // 645的左侧就是岩壁；把原700ms侧移分给两侧，不扩大恢复期限或到达预算。
        foreach (var direction in new[] { GIActions.MoveLeft, GIActions.MoveRight })
        {
            var stableDisplacement = 0;
            var origin = direction == GIActions.MoveLeft ? start : await scope.ObserveAsync();
            try
            {
                await scope.SendAsync(direction, KeyType.KeyDown);
                var started = scope.Io.Clock.GetTimestamp();
                while (scope.Io.Clock.GetElapsedTime(started).TotalMilliseconds < 350)
                {
                    await scope.DelayAsync((int)Math.Ceiling(Math.Min(60,
                        350 - scope.Io.Clock.GetElapsedTime(started).TotalMilliseconds)));
                    var observed = await scope.ObserveAsync();
                    var dx = observed.Position.X - origin.Position.X;
                    var dy = observed.Position.Y - origin.Position.Y;
                    stableDisplacement = dx * dx + dy * dy >= 4 ? stableDisplacement + 1 : 0;
                    if (stableDisplacement >= 2) return true;
                }
            }
            finally { await scope.SendAsync(direction, KeyType.KeyUp); }
        }
        return false;
    }
}
