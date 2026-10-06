using System;
using System.Runtime.ExceptionServices;
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
    private readonly string _request = "path-approach:" + Guid.NewGuid().ToString("N");

    internal void RecordPulse(PathApproachPulse pulse) => _pulse = pulse;
    internal void RecordRotation(int targetAngle, bool completed) => _rotation = $"targetAngle={targetAngle} completed={completed}";

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
                { ["nativeInput"] = _pulse.Receipt?.Describe() ?? "unknown:no-pulse-observed", ["rotation"] = _rotation },
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
