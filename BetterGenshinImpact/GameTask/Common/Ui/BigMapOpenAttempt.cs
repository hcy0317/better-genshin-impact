using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal enum BigMapOpenOutcome { Opened, TimedOut, NoVisualFeedback, SourceUnavailable, InputUnconfirmed }

/// <summary>开地图的实际原帧/输入/反馈边界；递增帧号不等于画面响应。</summary>
internal static class BigMapOpenAttempt
{
    internal static async Task<BigMapOpenOutcome> RunAsync(Func<ImageRegion> capture,
        Func<ImageRegion, bool> isMap, Func<ImageRegion, string?> connectionWait,
        Action focus, Action send, Func<int, CancellationToken, Task> delay, CancellationToken ct,
        TimeSpan budget, TimeProvider clock, Func<string> environment,
        Action<string, ImageRegion, string>? evidence = null)
    {
        var started = clock.GetTimestamp();
        void Check() { ct.ThrowIfCancellationRequested(); TaskExecutionScope.ThrowIfFailed(); UiOperation.Current?.Check(); }
        void Record(string phase, ImageRegion image, string detail)
        {
            try { evidence?.Invoke(phase, image, detail); } catch { /* 诊断不替换原失败。 */ }
        }
        void CheckWait(ImageRegion image)
        {
            if (connectionWait(image) is { } reason)
            {
                Record("network-wait", image, reason);
                throw new NetworkInterruptionException(reason);
            }
        }
        Check();
        focus();
        Check();
        using var before = capture();
        CheckWait(before);
        var wasMap = isMap(before);
        Check();
        if (before.SrcMat.Empty() || !before.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) || clock.GetElapsedTime(started) >= budget)
        {
            Record("source-unavailable", before, "输入前原源帧未知/过期或预算耗尽，未发送地图键");
            return BigMapOpenOutcome.SourceUnavailable;
        }
        if (wasMap) return BigMapOpenOutcome.Opened;
        string context;
        try { context = environment(); } catch { context = "unknown:environment-unavailable"; }
        Record("before", before, context);
        DiagnosticInputReceipt receipt;
        Check();
        if (!before.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) || clock.GetElapsedTime(started) >= budget)
        {
            Record("source-unavailable", before, "输入准入前原帧过期，未发送地图键");
            return BigMapOpenOutcome.SourceUnavailable;
        }
        using (var attempt = new DiagnosticInputAttempt(clock))
        {
            try { Check(); send(); receipt = attempt.Complete(true); }
            catch (Exception error)
            {
                Record("input-failed", before, attempt.Complete(false, error).Describe() + "; " + context);
                if (TaskFailureRecoveryPolicy.IsCancellation(error)) throw;
                var terminal = new TaskFailureRecoveryException(error,
                    new InvalidOperationException("开地图输入或松键失败，不重放输入或继续路线"));
                TaskExecutionScope.Capture().Report(terminal);
                throw terminal;
            }
        }
        Record("input-result", before, receipt.Describe() + "; " + context);
        if (receipt.Status != DiagnosticInputStatus.Sent)
        {
            Record("input-unconfirmed", before, receipt.Describe() + "; " + context);
            return BigMapOpenOutcome.InputUnconfirmed;
        }
        var fence = new CaptureFrameFence(before.FrameStamp, receipt.CompletedAt);
        var last = before.FrameStamp;
        var accepted = 0;
        var mapFrames = 0;
        var pixelsChanged = false;
        await delay(100, ct); // 松键后等待游戏30–60ms响应，再观察来源栅栏后的原帧。
        while (true)
        {
            Check();
            using var frame = capture();
            CheckWait(frame);
            var fresh = frame.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge) &&
                fence.Accepts(frame.FrameStamp) && frame.FrameStamp.IsAfter(last);
            var map = fresh && isMap(frame);
            Check();
            fresh &= frame.FrameStamp.IsFresh(clock, UiSnapshot.RecoveryMaximumAge);
            if (fresh)
            {
                last = frame.FrameStamp;
                accepted++;
                pixelsChanged |= frame.SrcMat.Size() != before.SrcMat.Size() || frame.SrcMat.Type() != before.SrcMat.Type() ||
                    Cv2.Norm(before.SrcMat, frame.SrcMat, NormTypes.INF) != 0;
                if (accepted == 1) Record("first-feedback", frame,
                    $"before={before.FrameStamp}; fence={fence.InputCompletedTimestamp}; pixelsChanged={pixelsChanged}; {receipt.Describe()}; {context}");
                mapFrames = map ? mapFrames + 1 : 0;
            }
            else mapFrames = 0;
            // Recognition may spend the remaining budget; an after-deadline map is not success.
            if (clock.GetElapsedTime(started) >= budget)
            {
                var result = accepted == 0 ? BigMapOpenOutcome.SourceUnavailable :
                    pixelsChanged ? BigMapOpenOutcome.TimedOut : BigMapOpenOutcome.NoVisualFeedback;
                Record("failure-before", before, $"outcome={result}; {receipt.Describe()}; {context}");
                Record("timeout", frame, $"outcome={result}; accepted={accepted}; pixelsChanged={pixelsChanged}; before={before.FrameStamp}; {receipt.Describe()}; {context}");
                return result;
            }
            if (mapFrames >= 2) return BigMapOpenOutcome.Opened;
            await delay(80, ct);
        }
    }

    internal static async Task<bool> CompleteAsync(BigMapOpenOutcome result, Exception? interruption,
        Func<Exception, CancellationToken, Task<bool>> waitForNetwork, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (interruption == null && result == BigMapOpenOutcome.Opened) return true;
        if (interruption == null && result == BigMapOpenOutcome.TimedOut) return false;
        var failure = interruption ?? new InvalidOperationException(
            $"[BGI_MAP_NO_FEEDBACK] 开地图未得到可验证反馈：{result}。采集、焦点、输入或游戏响应原因尚未确认，停止后续路线。");
        failure.Data["UI_NO_PROGRESS"] = true;
        try
        {
            // 延后的等待画面仍由原有被动网络窗口确认；普通HUD不能清除此失败。
            if (await waitForNetwork(failure, ct))
            {
                ct.ThrowIfCancellationRequested();
                TaskExecutionScope.EnsureNetworkReplaySafe(failure);
                throw new NetworkTaskRetryException(failure);
            }
            throw new TaskFailureRecoveryException(failure,
                new InvalidOperationException("无反馈且未确认网络恢复，不能仅凭HUD继续任务"));
        }
        catch (Exception error) when (error is NetworkTaskRetryException || TaskFailureRecoveryPolicy.IsCancellation(error))
        {
            TaskExecutionScope.Capture().Report(error);
            throw;
        }
        catch (Exception error)
        {
            var terminal = TaskFailureRecoveryPolicy.IsRecoveryFailure(error) ? error : new TaskFailureRecoveryException(failure, error);
            TaskExecutionScope.Capture().Report(terminal);
            throw terminal;
        }
    }
}
