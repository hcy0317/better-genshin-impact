using System;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal sealed class PreciseApproachRotationPolicy
{
    private readonly int _maxConsecutiveFailures;

    public PreciseApproachRotationPolicy(int maxConsecutiveFailures)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConsecutiveFailures, 1);
        _maxConsecutiveFailures = maxConsecutiveFailures;
    }

    public int ConsecutiveFailures { get; private set; }

    public bool Observe(bool rotated)
    {
        if (rotated)
        {
            ConsecutiveFailures = 0;
            return false;
        }

        ConsecutiveFailures++;
        return ConsecutiveFailures >= _maxConsecutiveFailures;
    }
}
