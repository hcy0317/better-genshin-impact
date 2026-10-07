using System;
using System.Runtime.ExceptionServices;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal readonly record struct PathApproachPulse(int Requested, int Submitted, bool Uncertain, double ElapsedMilliseconds,
    DiagnosticInputReceipt? Receipt = null)
{
    internal bool HasCompleteReceipt => !Uncertain &&
        ((Requested > 0 && Requested == Submitted) ||
         (Receipt is { TransportRequested: > 0, Status: DiagnosticInputStatus.Sent } &&
          Receipt.TransportRequested == Receipt.TransportAcknowledged));
}

/// <summary>仅记录既有精确接近的事实，不改变距离、方向或步数。</summary>
internal sealed class PathApproachDiagnostics(string route, string context)
{
    private Point2f? _previous;
    private CaptureFrameStamp _source;
    private int _stationary;
    private bool _captured;
    private PathApproachPulse _pulse;
    private string _rotation = "not-observed";
    private string _recoveryInput = "not-submitted";
    private string _recoveryEnvironment = "not-observed";
    private const int MaximumRecoveryInputs = 8;
    private readonly List<(GIActions Action, KeyType Type, DiagnosticInputReceipt Receipt, string Environment)> _recoveryInputs = new();
    private int _recoveryInputCount, _recoveryFeedbackInput;
    private PathMoveObservation? _recoveryStart, _recoveryEnd;
    private DiagnosticInputReceipt? _lastRecoveryReceipt;
    private string _recoveryOutcome = "in-progress";
    private readonly string _request = "path-approach:" + Guid.NewGuid().ToString("N");

    internal void RecordPulse(PathApproachPulse pulse) => _pulse = pulse;
    internal void RecordRotation(int targetAngle, bool completed) => _rotation = $"targetAngle={targetAngle} completed={completed}";

    internal void RecordRecoveryInput(GIActions action, KeyType type,
        DiagnosticInputReceipt receipt, string environment)
    {
        _recoveryInput = $"action={action} type={type}; {receipt.Describe()}";
        _recoveryEnvironment = environment;
        _lastRecoveryReceipt = receipt;
        _recoveryInputCount++;
        if (_recoveryInputs.Count < MaximumRecoveryInputs) _recoveryInputs.Add((action, type, receipt, environment));
    }

    private static string DescribeRecovery(PathMoveObservation? observation) => observation is { } value
        ? FormattableString.Invariant($"position=({value.Position.X},{value.Position.Y}) source={value.Stamp.SessionId}/{value.Stamp.Sequence} motion={value.Motion} valid={value.Valid} sourceUsable={value.SourceUsable}")
        : "unknown:no-observation";

    private Dictionary<string, string> RecoveryFields()
    {
        var fields = new Dictionary<string, string>
        {
            ["nativeInput"] = _recoveryInput, ["nativeInputEnvironment"] = _recoveryEnvironment, ["rotation"] = _rotation,
            ["recoveryStart"] = DescribeRecovery(_recoveryStart), ["recoveryEnd"] = DescribeRecovery(_recoveryEnd),
            ["recoveryOutcome"] = _recoveryOutcome,
            ["recoveryInputs"] = string.Join(";", _recoveryInputs.Select((item, index) => $"{index + 1}:{item.Receipt.RequestId:N}")),
            ["recoveryInputCoverage"] = $"total={_recoveryInputCount} recorded={_recoveryInputs.Count} omitted={_recoveryInputCount - _recoveryInputs.Count} limit={MaximumRecoveryInputs}"
        };
        for (var index = 0; index < _recoveryInputs.Count; index++)
            fields["recoveryInput" + (index + 1)] = $"action={_recoveryInputs[index].Action} type={_recoveryInputs[index].Type}; {_recoveryInputs[index].Receipt.Describe()}; {_recoveryInputs[index].Environment}";
        return fields;
    }

    internal void RecoveryFeedback(ImageRegion frame, PathMoveObservation observation, ILogger logger)
    {
        _recoveryEnd = observation;
        if (_recoveryFeedbackInput == _recoveryInputCount || _recoveryInputCount > MaximumRecoveryInputs ||
            _lastRecoveryReceipt is not { } input || frame.FrameStamp.TimestampFrequency != input.Frequency ||
            frame.FrameStamp.CapturedTimestamp - input.CompletedAt < input.Frequency * .06 ||
            _recoveryStart is not { } start || !frame.FrameStamp.IsAfter(start.Stamp)) return;
        _recoveryFeedbackInput = _recoveryInputCount;
        Recovery(frame, observation, "feedback-input-" + _recoveryInputCount, logger);
    }

    internal void RecoveryFinished(string outcome, ILogger logger)
    {
        _recoveryOutcome = outcome;
        try
        {
            var detail = $"route={route} {context} outcome={outcome}; last existing observation, not a new terminal screenshot; start={DescribeRecovery(_recoveryStart)} end={DescribeRecovery(_recoveryEnd)}";
            logger.LogDebug("PATH_APPROACH_RECOVERY_END request={Request} {Detail} inputs={Inputs}", _request, detail, RecoveryFields()["recoveryInputs"]);
            DiagnosticEvidenceScope.Current?.RequestLatestWindow(_request, "ground-recovery-end", detail, logger, RecoveryFields());
        }
        catch { }
    }

    internal void Recovery(ImageRegion frame, PathMoveObservation observation, string phase, ILogger logger)
    {
        try
        {
            if (phase == "before") _recoveryStart = observation;
            _recoveryEnd = observation;
            DiagnosticEvidenceScope.Current?.TryCapture(frame, _request, "ground-recovery-" + phase,
                $"route={route} {context} phase={phase} position={observation.Position} motion={observation.Motion} valid={observation.Valid} sourceUsable={observation.SourceUsable}; observed recovery, not arrival",
                logger, fields: RecoveryFields(),
                priority: DiagnosticEvidencePriority.Warning);
        }
        catch { }
    }

    internal void Detach(ImageRegion frame, int attempt, string phase, PathMoveObservation observation,
        PathApproachPulse pulse, string environment, CaptureFrameFence? fence, ILogger logger)
    {
        try
        {
            var fields = new System.Collections.Generic.Dictionary<string, string>
            {
                ["nativeInput"] = pulse.Receipt?.Describe() ?? "not-yet-submitted",
                ["nativeInputEnvironment"] = environment,
                ["detachFence"] = $"source={fence?.Before.SessionId}/{fence?.Before.Sequence} completed={fence?.InputCompletedTimestamp} accepted={fence?.Accepts(frame.FrameStamp)}"
            };
            var detail = $"route={route} {context} attempt={attempt}/2 phase={phase} position={observation.Position} motion={observation.Motion} valid={observation.Valid}";
            DiagnosticEvidenceScope.Current?.TryCapture(frame, _request, $"detach-{attempt}-{phase}", detail, logger, fields: fields);
            logger.LogDebug("PATH_APPROACH_DETACH_EVIDENCE request={Request} {Detail} receipt={Receipt} environment={Environment}",
                _request, detail, fields["nativeInput"], environment);
        }
        catch { }
    }

    internal void Exhausted(ImageRegion frame, PathPosition position, Point2f target, string motion, ILogger logger)
    {
        try
        {
            DiagnosticEvidenceScope.Current?.TryCapture(frame, _request, "precise-exhausted",
                $"route={route} {context} target={target} current={position.Point} locationSource={position.Source} motion={motion}; 25步耗尽的实际判定帧，不是恢复后截图",
                logger, fields: new System.Collections.Generic.Dictionary<string, string>
                { ["nativeInput"] = _pulse.Receipt?.Describe() ?? "unknown:no-pulse-observed", ["rotation"] = _rotation,
                    ["recoveryOutcome"] = _recoveryOutcome, ["recoveryInputs"] = RecoveryFields()["recoveryInputs"],
                    ["recoveryStart"] = DescribeRecovery(_recoveryStart), ["recoveryEnd"] = DescribeRecovery(_recoveryEnd) },
                priority: DiagnosticEvidencePriority.Warning);
        }
        catch { }
    }

    internal void Observe(ImageRegion frame, Point2f position, Point2f target, double distance, int step, ILogger logger,
        bool? directPosition = null, NavigationFrameEvidence navigation = default, string? motion = null,
        PathPositionSource locationSource = PathPositionSource.Unknown)
    {
        try
        {
            DiagnosticEvidenceScope.Current?.ObserveExistingFrame(frame);
            if (_captured || !frame.FrameStamp.IsKnown) return;
            var sourceAdvanced = !_source.IsKnown || frame.FrameStamp.IsAfter(_source);
            var sameSource = !_source.IsKnown || frame.FrameStamp.SessionId == _source.SessionId;
            _stationary = sameSource && _previous is { } previous &&
                Math.Abs(position.X - previous.X) + Math.Abs(position.Y - previous.Y) < .1 ? _stationary + 1 : 0;
            var previousPosition = _previous;
            _previous = position;
            _source = frame.FrameStamp;
            if (_stationary < 5 || distance < 2) return;
            _captured = true;
            var age = frame.FrameStamp.TimestampFrequency == TimeProvider.System.TimestampFrequency
                ? TimeProvider.System.GetElapsedTime(frame.FrameStamp.CapturedTimestamp).TotalMilliseconds : -1;
            var sourceKind = locationSource != PathPositionSource.Unknown ? locationSource.ToString().ToLowerInvariant()
                : directPosition == true ? "direct" : "unknown";
            var detail = FormattableString.Invariant($"{context} step={step}/25 target=({target.X:F2},{target.Y:F2}) current=({position.X:F2},{position.Y:F2}) distance={distance:F2} stationary={_stationary} locationSource={sourceKind} requested={_pulse.Requested} submitted={_pulse.Submitted} uncertain={_pulse.Uncertain} holdRequestedMs=60 pulseElapsedMs={_pulse.ElapsedMilliseconds:F2} sourceAdvanced={sourceAdvanced} sourceAgeMs={age:F2}; diagnostic only, stale/duplicate frames retained as such; no arrival-policy change");
            detail += $" previous=({previousPosition?.X},{previousPosition?.Y}) observedMotion={motion ?? "unknown:not-observed"} " + navigation.Describe();
            DiagnosticEvidenceScope.Current?.RequestWindowFromFrame(_request, "precise-stall", frame, "route=" + route + " " + detail, logger,
                fields: new System.Collections.Generic.Dictionary<string, string>
                { ["nativeInput"] = _pulse.Receipt?.Describe() ?? "unknown:no-pulse-observed" });
            logger.LogWarning("PATH_APPROACH_STALL {Route} {Detail}", route, detail);
        }
        catch { /* 原帧取证或日志故障不能改变路径结果。 */ }
    }

    internal static PathApproachPulse RunPulse(Action down, Action up, Action<int> wait, ImageRegion? before = null)
    {
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();
        using var input = new DiagnosticInputAttempt(clock);
        var entered = false;
        Exception? failure = null;
        using var capture = new InputDispatchCapture(() => entered = true);
        try { down(); wait(60); }
        catch (Exception error) { failure = error; }
        finally
        {
            if (entered)
            {
                try { up(); }
                catch (Exception cleanup) { failure = failure == null ? cleanup : new AggregateException(failure, cleanup); }
            }
        }
        return FinishPulse(capture, input, clock, started, failure, before);
    }

    internal static async System.Threading.Tasks.Task<PathApproachPulse> RunPulseAsync(Action down, Action up,
        Func<int, System.Threading.Tasks.Task> wait, TimeProvider clock, ImageRegion? before = null)
    {
        var started = clock.GetTimestamp();
        using var input = new DiagnosticInputAttempt(clock);
        var entered = false;
        Exception? failure = null;
        using var capture = new InputDispatchCapture(() => entered = true);
        try { down(); await wait(60); }
        catch (Exception error) { failure = error; }
        finally
        {
            if (entered)
            {
                try { up(); }
                catch (Exception cleanup) { failure = failure == null ? cleanup : new AggregateException(failure, cleanup); }
            }
        }
        return FinishPulse(capture, input, clock, started, failure, before);
    }

    private static PathApproachPulse FinishPulse(InputDispatchCapture capture, DiagnosticInputAttempt input,
        TimeProvider clock, long started, Exception? failure, ImageRegion? before)
    {
        var receipt = input.Complete(capture.HasDispatch, failure);
        if (failure != null)
        {
            try
            {
                var fields = new System.Collections.Generic.Dictionary<string, string> { ["nativeInput"] = receipt.Describe() };
                var request = "path-input:" + receipt.RequestId.ToString("N");
                if (before != null)
                    DiagnosticEvidenceScope.Current?.RequestWindowFromFrame(request, "path-input-failed", before,
                        "native pulse failed; business exception preserved", fields: fields);
                else
                    DiagnosticEvidenceScope.Current?.RequestLatestWindow(request, "path-input-failed",
                        "native pulse failed; business exception preserved; inputSource=unknown:not-supplied", fields: fields);
            }
            catch { /* 取证不能替换按下/释放的原始异常。 */ }
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return new(capture.Requested, capture.Submitted, capture.Uncertain,
            clock.GetElapsedTime(started).TotalMilliseconds, receipt);
    }
}
