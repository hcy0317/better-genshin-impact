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

// 返回值仅表示可交给原健康门核验的治疗尝试，不代表技能或回血成功。
internal static class PathPartyHealing
{
    internal static async Task<bool> TryAsync(IEnumerable<PathPartyHealingAvatar> avatars,
        Func<Task<bool>> restoreMain, Func<int, Task> delay, CancellationToken ct)
    {
        foreach (var avatar in avatars)
        {
            ct.ThrowIfCancellationRequested();
            if (avatar.Name == "白术")
            {
                if (avatar.TrySwitch())
                {
                    ct.ThrowIfCancellationRequested();
                    avatar.Skill();
                    await delay(800);
                    ct.ThrowIfCancellationRequested();
                    avatar.Skill();
                    await delay(800);
                    await RestoreMainAsync(restoreMain, ct);
                    await delay(4000);
                    return true;
                }
                break;
            }
            else if (avatar.Name == "希格雯")
            {
                if (avatar.TrySwitch())
                {
                    ct.ThrowIfCancellationRequested();
                    avatar.Skill();
                    await delay(11000);
                    await RestoreMainAsync(restoreMain, ct);
                    return true;
                }
                break;
            }
            else if (avatar.Name == "珊瑚宫心海")
            {
                if (avatar.TrySwitch())
                {
                    ct.ThrowIfCancellationRequested();
                    avatar.Skill();
                    await delay(500);
                    ct.ThrowIfCancellationRequested();
                    avatar.Burst();
                    await RestoreMainAsync(restoreMain, ct);
                    await delay(5000);
                    return true;
                }
            }
            else if (avatar.Name == "琴")
            {
                var selected = avatar.TrySwitch();
                ct.ThrowIfCancellationRequested();
                if (!selected)
                {
                    // 未确认选中不能推定角色没变；回原行走位后才允许fallback。
                    await RestoreMainAsync(restoreMain, ct);
                    return false;
                }
                var result = avatar.NativeBurst();
                ct.ThrowIfCancellationRequested();
                await RestoreMainAsync(restoreMain, ct);
                return result is BurstCastResult.Confirmed or BurstCastResult.Unconfirmed;
            }
        }
        return false;
    }

    private static async Task RestoreMainAsync(Func<Task<bool>> restoreMain, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var restored = await restoreMain();
        ct.ThrowIfCancellationRequested();
        if (!restored)
            throw new InvalidOperationException("队内治疗后未确认行走角色，禁止用其他角色血量确认恢复");
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
