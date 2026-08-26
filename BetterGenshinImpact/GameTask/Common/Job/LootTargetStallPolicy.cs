using System;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal sealed class LootTargetStallPolicy
{
    private readonly int _requiredRepeatedPickupAttempts;
    private readonly int _coordinateTolerance;
    private Rect? _lastTarget;
    private int _repeatedPickupAttempts;

    internal LootTargetStallPolicy(int requiredRepeatedPickupAttempts, int coordinateTolerance)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(requiredRepeatedPickupAttempts, 2);
        ArgumentOutOfRangeException.ThrowIfNegative(coordinateTolerance);
        _requiredRepeatedPickupAttempts = requiredRepeatedPickupAttempts;
        _coordinateTolerance = coordinateTolerance;
    }

    internal bool Observe(Rect target, bool pickupAttempted)
    {
        if (!pickupAttempted)
        {
            Reset();
            return false;
        }

        if (_lastTarget is { } previous && IsSameTarget(previous, target))
        {
            _repeatedPickupAttempts++;
        }
        else
        {
            _lastTarget = target;
            _repeatedPickupAttempts = 1;
        }

        return _repeatedPickupAttempts >= _requiredRepeatedPickupAttempts;
    }

    internal void Reset()
    {
        _lastTarget = null;
        _repeatedPickupAttempts = 0;
    }

    private bool IsSameTarget(Rect left, Rect right)
    {
        return Math.Abs(left.X - right.X) <= _coordinateTolerance
               && Math.Abs(left.Y - right.Y) <= _coordinateTolerance
               && Math.Abs(left.Width - right.Width) <= _coordinateTolerance
               && Math.Abs(left.Height - right.Height) <= _coordinateTolerance;
    }
}
