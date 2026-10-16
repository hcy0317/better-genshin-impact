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
        var stableDisplacement = 0;
        try
        {
            await scope.SendAsync(GIActions.MoveLeft, KeyType.KeyDown);
            var started = scope.Io.Clock.GetTimestamp();
            // No longer than the existing first lateral recovery hold (700ms).
            while (scope.Io.Clock.GetElapsedTime(started).TotalMilliseconds < 700)
            {
                await scope.DelayAsync((int)Math.Ceiling(Math.Min(60,
                    700 - scope.Io.Clock.GetElapsedTime(started).TotalMilliseconds)));
                var observed = await scope.ObserveAsync();
                var dx = observed.Position.X - start.Position.X;
                var dy = observed.Position.Y - start.Position.Y;
                stableDisplacement = dx * dx + dy * dy >= 4 ? stableDisplacement + 1 : 0;
                if (stableDisplacement >= 2) return true;
            }
            return false;
        }
        finally { await scope.SendAsync(GIActions.MoveLeft, KeyType.KeyUp); }
    }
}
