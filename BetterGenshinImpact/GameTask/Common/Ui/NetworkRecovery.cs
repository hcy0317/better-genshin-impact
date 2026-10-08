using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal sealed class NetworkInterruptionException(string reason) : Exception("自动化输入已暂停：" + reason)
{
    internal string Reason { get; } = reason;
}

// Issued after a passive stable-frame candidate. The host must verify the normal
// safe UI handoff before replay, and still requires a real task completion result.
internal sealed class NetworkTaskRetryException(Exception original) : Exception("网络或服务器等待已恢复，重新执行当前任务", original);

internal static class NetworkRecovery
{
    internal static async Task<bool> WaitAsync(bool confirmedInterruption, Func<UiSnapshot> capture,
        Action release, Func<bool> networkAvailable, Func<int, CancellationToken, Task> delay,
        CancellationToken ct, TimeProvider? clock = null, Action<string, UiSnapshot?>? trace = null)
    {
        clock ??= TimeProvider.System;
        var started = clock.GetTimestamp();
        var interrupted = confirmedInterruption;
        UiSnapshot? previous = null;
        long? readySince = null;
        var readyFrames = 0;
        var blockedFrames = 0;
        ct.ThrowIfCancellationRequested();
        release(); // A release failure propagates; no replay is allowed after failed cleanup.
        void Trace(string state, UiSnapshot? observed)
        {
            try { trace?.Invoke(state, observed); }
            catch { /* Diagnostics do not own recovery or input permission. */ }
        }
        Trace(interrupted ? "waiting" : "suspected-no-feedback", null);
        while (clock.GetElapsedTime(started) < TimeSpan.FromMinutes(10))
        {
            ct.ThrowIfCancellationRequested();
            var online = networkAvailable(); // A live adapter is not itself proof of game/server recovery.
            var observed = capture();
            ct.ThrowIfCancellationRequested();
            var fresh = observed.SourceBound
                ? observed.SourceStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) : observed.FrameId > 0;
            var advancing = previous == null || observed.IsAfter(previous);
            if (!online || fresh && advancing && observed.NetworkWaitReason != null)
            {
                if (!online || observed.NetworkWaitReason != "connection-panel-unconfirmed" && ++blockedFrames >= 2)
                {
                    if (!interrupted) Trace("confirmed-wait", observed);
                    interrupted = true;
                }
                readyFrames = 0;
                readySince = null;
            }
            else if (interrupted && fresh && advancing && observed.HasUsableEvidence &&
                     (observed.MainReady || observed.MapReady) &&
                     (previous == null || !observed.SourceBound || observed.SourceStamp.SessionId == previous.SourceStamp.SessionId))
            {
                blockedFrames = 0;
                if (readySince != null && previous?.Signature != observed.Signature)
                {
                    readySince = null;
                    readyFrames = 0;
                }
                readySince ??= clock.GetTimestamp();
                if (++readyFrames >= 3 && clock.GetElapsedTime(readySince.Value) >= TimeSpan.FromSeconds(3))
                {
                    Trace("stable-candidate-verify-before-retry", observed);
                    return true;
                }
            }
            else
            {
                blockedFrames = 0;
                readyFrames = 0;
                readySince = null;
            }
            previous = observed;
            // Leave time for the delayed game icon. Mere unresponsiveness never authorizes replay.
            if (!interrupted && clock.GetElapsedTime(started) >= TimeSpan.FromMinutes(3))
            {
                Trace("unconfirmed-stop", observed);
                return false;
            }
            await delay(1000, ct);
        }
        ct.ThrowIfCancellationRequested();
        throw new TimeoutException("网络恢复等待超过10分钟，未确认新鲜且稳定的可操作画面，停止当前任务");
    }
}
