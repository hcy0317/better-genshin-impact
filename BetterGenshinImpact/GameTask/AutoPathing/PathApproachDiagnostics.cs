using System;
using System.Runtime.ExceptionServices;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal readonly record struct PathApproachPulse(int Requested, int Submitted, bool Uncertain, double ElapsedMilliseconds);

/// <summary>仅记录既有精确接近的事实，不改变距离、方向或步数。</summary>
internal sealed class PathApproachDiagnostics(string route, string context)
{
    private Point2f? _previous;
    private CaptureFrameStamp _source;
    private int _stationary;
    private bool _captured;
    private PathApproachPulse _pulse;

    internal void RecordPulse(PathApproachPulse pulse) => _pulse = pulse;

    internal void Observe(ImageRegion frame, Point2f position, Point2f target, double distance, int step, ILogger logger, bool? directPosition = null)
    {
        try
        {
            if (_captured || !frame.FrameStamp.IsKnown) return;
            var sourceAdvanced = !_source.IsKnown || frame.FrameStamp.IsAfter(_source);
            var sameSource = !_source.IsKnown || frame.FrameStamp.SessionId == _source.SessionId;
            _stationary = sameSource && _previous is { } previous &&
                Math.Abs(position.X - previous.X) + Math.Abs(position.Y - previous.Y) < .1 ? _stationary + 1 : 0;
            _previous = position;
            _source = frame.FrameStamp;
            if (_stationary < 5 || distance < 2) return;
            _captured = true;
            var age = frame.FrameStamp.TimestampFrequency == TimeProvider.System.TimestampFrequency
                ? TimeProvider.System.GetElapsedTime(frame.FrameStamp.CapturedTimestamp).TotalMilliseconds : -1;
            var detail = FormattableString.Invariant($"{context} step={step}/25 target=({target.X:F2},{target.Y:F2}) current=({position.X:F2},{position.Y:F2}) distance={distance:F2} stationary={_stationary} locationSource={(directPosition == true ? "direct" : directPosition == false ? "fallback-or-invalid" : "unknown")} requested={_pulse.Requested} submitted={_pulse.Submitted} uncertain={_pulse.Uncertain} holdRequestedMs=60 pulseElapsedMs={_pulse.ElapsedMilliseconds:F2} sourceAdvanced={sourceAdvanced} sourceAgeMs={age:F2}; diagnostic only, stale/duplicate frames retained as such; no arrival-policy change");
            DiagnosticEvidenceScope.Current?.TryCapture(frame, "path-approach:" + route + ":" + context, "precise-stall", detail, logger);
            logger.LogWarning("PATH_APPROACH_STALL {Route} {Detail}", route, detail);
        }
        catch { /* 原帧取证或日志故障不能改变路径结果。 */ }
    }

    internal static PathApproachPulse RunPulse(Action down, Action up, Action<int> wait)
    {
        var clock = TimeProvider.System;
        var started = clock.GetTimestamp();
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
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return new(capture.Requested, capture.Submitted, capture.Uncertain, clock.GetElapsedTime(started).TotalMilliseconds);
    }

    internal static async System.Threading.Tasks.Task<PathApproachPulse> RunPulseAsync(Action down, Action up,
        Func<int, System.Threading.Tasks.Task> wait, TimeProvider clock)
    {
        var started = clock.GetTimestamp();
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
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return new(capture.Requested, capture.Submitted, capture.Uncertain, clock.GetElapsedTime(started).TotalMilliseconds);
    }
}
