using System;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal sealed class LootScanCompletionPolicy
{
    private readonly int _requiredEmptySweeps;
    private readonly TimeSpan _minimumScanDuration;

    public LootScanCompletionPolicy(int requiredEmptySweeps, TimeSpan minimumScanDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredEmptySweeps, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumScanDuration, TimeSpan.Zero);

        _requiredEmptySweeps = requiredEmptySweeps;
        _minimumScanDuration = minimumScanDuration;
    }

    public int ConsecutiveEmptySweeps { get; private set; }

    public bool ObserveSweep(bool foundItems, TimeSpan elapsed)
    {
        if (foundItems)
        {
            ConsecutiveEmptySweeps = 0;
            return false;
        }

        ConsecutiveEmptySweeps++;
        return ConsecutiveEmptySweeps >= _requiredEmptySweeps && elapsed >= _minimumScanDuration;
    }
}
