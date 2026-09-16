using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal readonly record struct CombatNativeInputRequest(Guid Id, CaptureFrameStamp Source, long DeadlineTimestamp);
internal sealed class CombatInputNotAdmittedException() : Exception("原动作未准入物理输入");

/// <summary>在真正的SendInput边界验证首个输入，并以原生返回数量产生回执；不拥有调度或恢复。</summary>
internal sealed class CombatNativeInput(TimeProvider clock, ILogger logger, Action prepare)
{
    internal CombatBattleHostInputResult Submit(CombatNativeInputRequest request, string kind,
        Action send, CancellationToken ct, Action? beforeFirstNative = null)
    {
        var requestedAt = clock.GetTimestamp();
        long? startedAt = null;
        Exception? failure = null;
        void CheckAdmission()
        {
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            if (request.Id == Guid.Empty || clock.GetTimestamp() >= request.DeadlineTimestamp ||
                clock.GetElapsedTime(requestedAt).TotalMilliseconds > 150 ||
                !request.Source.IsFresh(clock, TimeSpan.FromMilliseconds(150)))
                throw new TimeoutException("原生输入准入迟到或源帧过期，未发送输入");
        }

        using var capture = new InputDispatchCapture(() =>
        {
            // Windows输入器的焦点恢复也可能耗时，必须在它之后再检查。
            CheckAdmission();
            beforeFirstNative?.Invoke();
            CheckAdmission();
            startedAt = clock.GetTimestamp();
        });
        try
        {
            CheckAdmission();
            prepare();
            CheckAdmission();
            send();
        }
        catch (Exception error) { failure = error; }
        var returnedAt = clock.GetTimestamp();
        var result = Classify(capture, failure, returnedAt) with
        {
            NativeRequested = capture.Requested, NativeSubmitted = capture.Submitted,
            StartedTimestamp = startedAt, ObservableAfterTimestamp = returnedAt
        };
        try
        {
            logger.LogDebug("NATIVE_INPUT_RESULT request={Request} kind={Kind} status={Status} sourceSequence={Source} requested={Requested} submitted={Submitted} uncertain={Uncertain} startedAt={StartedAt} completedAt={CompletedAt} elapsedMs={Elapsed:F3} decisionMs={Decision} reason={Reason} errorType={ErrorType}",
                request.Id, kind, result.Status, request.Source.Sequence, capture.Requested, capture.Submitted,
                capture.Uncertain, startedAt, result.CompletedTimestamp, clock.GetElapsedTime(requestedAt).TotalMilliseconds,
                startedAt is { } started ? clock.GetElapsedTime(requestedAt, started).TotalMilliseconds : null,
                result.Reason, failure?.GetType().Name);
        }
        catch { /* 诊断不能改变物理输入结果。 */ }
        if (failure is OperationCanceledException) ExceptionDispatchInfo.Capture(failure).Throw();
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        return result;
    }

    internal static CombatBattleHostInputResult Classify(InputDispatchCapture capture, Exception? error, long returnedAt)
    {
        if (capture.Uncertain || capture.Submitted > 0 && capture.Submitted != capture.Requested)
            return new(CombatBattleHostInputStatus.Unknown, Reason: "native-input-partial-or-unknown", Error: error);
        if (capture.Submitted > 0)
            // 完整提交是不可回退的事实；执行阶段的后置错误由消费者在保存fence之后处理。
            return new(CombatBattleHostInputStatus.Sent, returnedAt,
                error == null ? null : "input-submitted-before-interruption", error);
        if (capture.NativeCalls > 0)
            return new(CombatBattleHostInputStatus.Failed, Reason: "native-input-rejected", Error: error);
        return error == null || error is TimeoutException or CombatInputNotAdmittedException or Script.Flow.CombatActionInterruptedException
            ? new(CombatBattleHostInputStatus.NotSent, Reason: error == null ? "no-native-input" : "not-admitted-before-native", Error: error)
            : new(CombatBattleHostInputStatus.Failed, Reason: "input-preparation-failed", Error: error);
    }
}
