using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.Common;

internal readonly record struct HealingFrame(CaptureFrameStamp Source, bool MainReady, bool? LowHp);

internal static class HealingObservation
{
    internal static async Task<bool> WaitAsync(CaptureFrameFence fence, Func<HealingFrame> capture,
        Func<int, Task> delay, CancellationToken ct, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var started = clock.GetTimestamp();
        while (clock.GetElapsedTime(started) < UiSnapshot.RecoveryMaximumAge)
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            var frame = capture();
            ct.ThrowIfCancellationRequested();
            if (clock.GetElapsedTime(started) >= UiSnapshot.RecoveryMaximumAge) break;
            // 传送/技能返回并不等于下一帧血条已更新；低血是尚未完成，而不是等待的终态。
            // 仍只在原两秒预算内接受输入后的新鲜非低血画面，不重发恢复输入。
            if (fence.Accepts(frame.Source) && frame.Source.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) && frame.MainReady && frame.LowHp is false)
                return true;
            await delay(100);
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }
}
