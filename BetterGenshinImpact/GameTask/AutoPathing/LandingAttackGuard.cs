using System;
using System.Threading;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>移动辅助只在即时确认飞行时允许下落攻击，不把地面脱困变成普攻。</summary>
internal static class LandingAttackGuard
{
    internal static bool TryAttack(Func<MotionStatus> observeMotion, Action attack, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var motion = observeMotion();
        ct.ThrowIfCancellationRequested();
        if (motion != MotionStatus.Fly) return false;
        attack();
        return true;
    }
}
