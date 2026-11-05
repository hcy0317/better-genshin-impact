using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal sealed record PathPartyHealingAvatar(string Name, Func<bool> TrySwitch,
    Action Skill, Action Burst, Func<BurstCastResult> NativeBurst);

internal readonly record struct PathPartyBurstFrame(CaptureFrameStamp Source, bool MainReady,
    int ActiveIndex, BurstObservation Burst);

// 结果区分治疗尝试和唯一待恢复行走位的交接；任何结果都不代替原健康门。
internal static class PathPartyHealing
{
    internal abstract class Outcome
    {
        private Outcome() { }
        internal sealed class NotAttempted : Outcome { }
        internal sealed class Attempted : Outcome { }
        internal sealed class PendingMainRestore : Outcome
        {
            private Func<Task<bool>>? _restoreMain;
            private readonly Action _checkParent;
            private readonly CancellationToken _ct;

            internal int ExpectedMainIndex { get; }

            internal PendingMainRestore(Func<Task<bool>> restoreMain, Action checkParent, CancellationToken ct, int expectedMainIndex)
            {
                ExpectedMainIndex = expectedMainIndex;
                _restoreMain = restoreMain;
                _checkParent = checkParent;
                _ct = ct;
            }

            internal async Task RecoverAtStatueAsync(Func<Task> recoverAtStatue)
            {
                CheckParent(_ct, _checkParent);
                var restore = Interlocked.Exchange(ref _restoreMain, null)
                    ?? throw new InvalidOperationException("行走位恢复交接已使用，禁止重复选择");
                await recoverAtStatue();
                CheckParent(_ct, _checkParent);
                await RestoreMainAsync(restore, _ct, _checkParent);
            }
        }
    }

    internal static async Task<Outcome> TryAsync(IEnumerable<PathPartyHealingAvatar> avatars,
        Func<Task<bool>> restoreMain, Func<int, Task> delay, CancellationToken ct, Action checkParent, int expectedMainIndex)
    {
        CheckParent(ct, checkParent);
        if (expectedMainIndex is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(expectedMainIndex), "未确认行走位编号，禁止开始队内治疗");
        foreach (var avatar in avatars)
        {
            CheckParent(ct, checkParent);
            if (avatar.Name == "白术")
            {
                if (avatar.TrySwitch())
                {
                    CheckParent(ct, checkParent);
                    avatar.Skill();
                    await delay(800);
                    CheckParent(ct, checkParent);
                    avatar.Skill();
                    await delay(800);
                    await RestoreMainAsync(restoreMain, ct, checkParent);
                    await delay(4000);
                    return new Outcome.Attempted();
                }
                break;
            }
            else if (avatar.Name == "希格雯")
            {
                if (avatar.TrySwitch())
                {
                    CheckParent(ct, checkParent);
                    avatar.Skill();
                    await delay(11000);
                    await RestoreMainAsync(restoreMain, ct, checkParent);
                    return new Outcome.Attempted();
                }
                break;
            }
            else if (avatar.Name == "珊瑚宫心海")
            {
                if (avatar.TrySwitch())
                {
                    CheckParent(ct, checkParent);
                    avatar.Skill();
                    await delay(500);
                    CheckParent(ct, checkParent);
                    avatar.Burst();
                    await RestoreMainAsync(restoreMain, ct, checkParent);
                    await delay(5000);
                    return new Outcome.Attempted();
                }
            }
            else if (avatar.Name == "琴")
            {
                var selected = avatar.TrySwitch();
                CheckParent(ct, checkParent);
                if (!selected)
                {
                    // 未确认选中不能推定角色没变；回原行走位后才允许fallback。
                    await RestoreMainAsync(restoreMain, ct, checkParent);
                    return new Outcome.NotAttempted();
                }
                var result = avatar.NativeBurst();
                CheckParent(ct, checkParent);
                if (result is BurstCastResult.NotReady or BurstCastResult.Unknown)
                    return new Outcome.PendingMainRestore(restoreMain, checkParent, ct, expectedMainIndex);
                if (result is not (BurstCastResult.Confirmed or BurstCastResult.Unconfirmed))
                    throw new InvalidOperationException("未识别的治疗施放结果，禁止交接恢复");
                await RestoreMainAsync(restoreMain, ct, checkParent);
                return new Outcome.Attempted();
            }
        }
        return new Outcome.NotAttempted();
    }

    private static async Task RestoreMainAsync(Func<Task<bool>> restoreMain, CancellationToken ct, Action checkParent)
    {
        CheckParent(ct, checkParent);
        var restored = await restoreMain();
        CheckParent(ct, checkParent);
        if (!restored)
            throw new InvalidOperationException("队内治疗后未确认行走角色，禁止用其他角色血量确认恢复");
    }

    private static void CheckParent(CancellationToken ct, Action checkParent)
    {
        ct.ThrowIfCancellationRequested();
        checkParent();
        ct.ThrowIfCancellationRequested();
    }

    internal static BurstCastResult TryGuardedBurst(Func<Func<bool>, BurstCastResult> nativeBurst,
        Func<PathPartyBurstFrame> capture, CaptureFrameStamp before, int expectedHealer,
        TimeSpan maximumAge, Action checkParent, TimeProvider clock)
    {
        checkParent();
        var started = clock.GetTimestamp();
        return nativeBurst(() =>
        {
            checkParent();
            var frame = capture();
            // 识别可能耗时；准入返回前再次检查父scope、来源和剩余预算。
            checkParent();
            return CanAdmitBurst(frame, before, expectedHealer, maximumAge, clock, started);
        });
    }

    internal static bool CanAdmitBurst(PathPartyBurstFrame frame, CaptureFrameStamp before,
        int expectedHealer, TimeSpan maximumAge, TimeProvider clock, long started) =>
        before.IsKnown && frame.Source.IsKnown && frame.Source.SessionId == before.SessionId &&
        frame.Source.TimestampFrequency == before.TimestampFrequency && frame.Source.Sequence > before.Sequence &&
        frame.Source.CapturedTimestamp > before.CapturedTimestamp && frame.Source.IsFresh(clock, maximumAge) &&
        frame.MainReady && expectedHealer is >= 1 and <= 4 && frame.ActiveIndex == expectedHealer &&
        frame.Burst.Ready && clock.GetElapsedTime(started) < TimeSpan.FromSeconds(2.4);

    internal static HealingFrame HealthFrame(CaptureFrameStamp source, bool mainReady,
        int actualActive, int expectedActive, bool? lowHp) =>
        new(source, mainReady && expectedActive is >= 1 and <= 4 && actualActive == expectedActive, lowHp);
}
