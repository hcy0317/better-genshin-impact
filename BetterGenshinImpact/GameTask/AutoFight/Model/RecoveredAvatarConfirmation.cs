using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

/// <summary>恢复阶段只确认指定角色；未知观察让出剩余期限，不消耗固定帧数配额。</summary>
internal static class RecoveredAvatarConfirmation
{
    internal static async Task<ReviveRecoveryFrame> WaitAsync(ReviveTarget? target,
        Func<ReviveRecoveryFrame> capture, Action<int> select, Func<int, Task> delay,
        TimeSpan timeout, CancellationToken ct, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var started = clock.GetTimestamp();
        ReviveRecoveryFrame? last = null;
        CaptureFrameFence? fence = null;
        var consecutive = 0;
        var nextInputAt = TimeSpan.Zero;
        while (clock.GetElapsedTime(started) < timeout)
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            var observed = capture();
            ct.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= timeout) break;
            var valid = observed.HasUsableEvidence && observed.SourceBound &&
                observed.SourceStamp.CapturedTimestamp > started &&
                (fence == null || fence.Value.Accepts(observed.SourceStamp));
            if (valid && last != null && !observed.IsAfter(last)) valid = false;
            if (valid)
            {
                last = observed;
                if (observed.Revive != ReviveUiState.None)
                    throw new InvalidOperationException($"神像恢复后仍出现复苏提示，未确认 {target?.Name ?? "主界面"} 恢复，不允许重试路线");
                if (target != null && observed.Party.TryGetValue(target.Index, out var name) && name != target.Name)
                    throw new InvalidOperationException($"神像恢复后目标槽位身份改变，期望 {target.Name}，实际 {name}");
                if (observed.Confirms(target))
                {
                    if (++consecutive >= 2) return observed;
                }
                else
                {
                    consecutive = 0;
                    if (target != null && observed.MainReady && observed.HasTarget(target) &&
                        clock.GetElapsedTime(started) >= nextInputAt)
                    {
                        ct.ThrowIfCancellationRequested();
                        select(target.Index);
                        fence = new(observed.SourceStamp, clock.GetTimestamp());
                        nextInputAt = clock.GetElapsedTime(started) + TimeSpan.FromSeconds(1);
                    }
                }
            }
            else consecutive = 0;
            var remaining = timeout - clock.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            await delay((int)Math.Min(250, Math.Ceiling(remaining.TotalMilliseconds)));
        }
        ct.ThrowIfCancellationRequested();
        throw new InvalidOperationException($"神像恢复后未取得 {target?.Name ?? "主界面"} 的稳定证据，不允许重试路线");
    }
}
