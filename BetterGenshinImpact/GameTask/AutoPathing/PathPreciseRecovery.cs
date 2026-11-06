using System;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator.Extensions;

namespace BetterGenshinImpact.GameTask.AutoPathing;

// Displacement ends only this probe; it never confirms arrival or replenishes steps.
internal static class PathPreciseRecovery
{
    internal static async Task<bool> SidestepAsync(PathRecoveryScope scope, PathMoveObservation start)
    {
        foreach (var direction in new[] { GIActions.MoveLeft, GIActions.MoveRight })
        {
            var stableDisplacement = 0;
            var origin = direction == GIActions.MoveLeft ? start : await scope.ObserveAsync(failOnDuplicate: true);
            bool Displaced(PathMoveObservation observed)
            {
                var dx = observed.Position.X - origin.Position.X;
                var dy = observed.Position.Y - origin.Position.Y;
                stableDisplacement = dx * dx + dy * dy >= 4 ? stableDisplacement + 1 : 0;
                return stableDisplacement >= 2;
            }
            var stopHolding = false;
            try
            {
                await scope.SendAsync(direction, KeyType.KeyDown);
                var started = scope.Io.Clock.GetTimestamp();
                while (!stopHolding && scope.Io.Clock.GetElapsedTime(started).TotalMilliseconds < 350)
                    stopHolding = await scope.DelayUntilAsync((int)Math.Ceiling(Math.Min(60,
                        350 - scope.Io.Clock.GetElapsedTime(started).TotalMilliseconds)), Displaced);
            }
            finally { await scope.SendAsync(direction, KeyType.KeyUp); }

            if (!stopHolding) continue;
            // Held feedback only stops the key. Use distinct post-release frames under
            // the unchanged input fence before skipping the opposite direction.
            stableDisplacement = 0;
            for (var observed = 0; observed < 2; observed++)
            {
                var released = await scope.ObserveAsync(failOnDuplicate: true);
                var dx = released.Position.X - origin.Position.X;
                var dy = released.Position.Y - origin.Position.Y;
                if (dx * dx + dy * dy < 4) return false;
                if (Displaced(released)) return true;
            }
            return false;
        }
        return false;
    }
}
