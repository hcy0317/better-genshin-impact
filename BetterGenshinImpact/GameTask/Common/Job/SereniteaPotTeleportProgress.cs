using System;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal sealed class SereniteaPotTeleportProgress
{
    private readonly int _requiredConsecutiveMapClosedChecks;
    private int _consecutiveMapClosedChecks;

    internal SereniteaPotTeleportProgress(int requiredConsecutiveMapClosedChecks = 2)
    {
        if (requiredConsecutiveMapClosedChecks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(requiredConsecutiveMapClosedChecks));
        }

        _requiredConsecutiveMapClosedChecks = requiredConsecutiveMapClosedChecks;
    }

    internal bool Observe(bool isInBigMapUi)
    {
        if (isInBigMapUi)
        {
            _consecutiveMapClosedChecks = 0;
            return false;
        }

        _consecutiveMapClosedChecks++;
        return _consecutiveMapClosedChecks >= _requiredConsecutiveMapClosedChecks;
    }
}
