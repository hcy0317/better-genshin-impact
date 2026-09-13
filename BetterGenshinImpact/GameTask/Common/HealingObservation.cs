using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.Common;

internal readonly record struct HealingFrame(CaptureFrameStamp Source, bool MainReady, bool LowHp);

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
            if (fence.Accepts(frame.Source) && frame.Source.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) && frame.MainReady)
                return !frame.LowHp;
            await delay(100);
        }
        ct.ThrowIfCancellationRequested();
        return false;
    }
}
