using System;
using System.Numerics;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal sealed class ArtifactScanDetailChangeDetector(
    double initialSignature,
    double tolerance)
{
    internal bool Observe(double detailSignature) =>
        Math.Abs(detailSignature - initialSignature) > tolerance;
}

internal sealed class ArtifactCharacterDetailChangeDetector(
    ulong initialSignature,
    int maximumStableDistance)
{
    internal bool Observe(ulong detailSignature) =>
        BitOperations.PopCount(detailSignature ^ initialSignature)
        > maximumStableDistance;
}

internal sealed class ArtifactDetailSwitchDetector(double initialSignature, double tolerance)
{
    private const int RequiredStableFrames = 2;
    private bool _changed;
    private double _lastDetailSignature = initialSignature;
    private double _lastLockSignature;
    private int _stableFrames;

    internal bool Observe(double detailSignature, double lockSignature)
    {
        if (!_changed)
        {
            if (Math.Abs(detailSignature - initialSignature) > tolerance)
            {
                _changed = true;
                _lastDetailSignature = detailSignature;
                _lastLockSignature = lockSignature;
                _stableFrames = 1;
            }
            return false;
        }

        var stable = Math.Abs(detailSignature - _lastDetailSignature) <= tolerance &&
                     Math.Abs(lockSignature - _lastLockSignature) <= tolerance;
        _stableFrames = stable ? _stableFrames + 1 : 1;
        _lastDetailSignature = detailSignature;
        _lastLockSignature = lockSignature;
        return _stableFrames >= RequiredStableFrames;
    }
}
