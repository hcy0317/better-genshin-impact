using System;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal readonly record struct PathMovementObservation(CaptureFrameStamp Source, string MoveMode, string Action,
    bool? Hud, int? ActiveSlot, bool? LowHp, string? LegacyMotionHint);

/// <summary>借用导航已有帧，只记录未知/观察事实；不授予输入、不另抓图、不决定恢复。</summary>
internal sealed class PathMovementDiagnostics(ILogger logger, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _next;
    private string? _point;
    private bool _wasAbnormal;
    private int _episode;
    internal void Observe(string point, Func<PathMovementObservation> read, ImageRegion? frame = null, string? route = null)
    {
        try
        {
            if (!logger.IsEnabled(LogLevel.Debug)) return;
            var now = _clock.GetTimestamp();
            if (_point == point && now < _next) return;
            _point = point;
            _next = now + _clock.TimestampFrequency;
            var sample = read();
            if (frame != null && DiagnosticEvidenceScope.Current is { } evidence)
            {
                var abnormal = sample.Hud is false || sample.Hud is true && sample.ActiveSlot == null || sample.LowHp is true;
                if (_wasAbnormal && !abnormal) _episode++;
                var request = $"path:{route}:{_episode}:{point}";
                var detail = $"point={point} route={route} hud={sample.Hud} slot={sample.ActiveSlot} lowHp={sample.LowHp} motion={sample.LegacyMotionHint}";
                if (abnormal) evidence.CaptureFault(frame, "path", request, _wasAbnormal ? "unresolved" : "abnormal", detail, logger,
                    allowPreviousRequest: true);
                else evidence.RememberBefore(frame, "path", request, detail);
                _wasAbnormal = abnormal;
            }
            logger.LogDebug("PATH_FRAME point={Point} sourceSession={Session} sourceSequence={Sequence} sourceAgeMs={Age} move={Move} action={Action} hud={Hud} activeSlot={Slot} lowHp={LowHp} legacyMotionHint={Motion} observationMs={Cost:F2} gameplayProgress=unknown",
                point, sample.Source.SessionId, sample.Source.Sequence,
                sample.Source.IsKnown ? _clock.GetElapsedTime(sample.Source.CapturedTimestamp).TotalMilliseconds : (double?)null,
                sample.MoveMode, sample.Action, sample.Hud, sample.ActiveSlot, sample.LowHp, sample.LegacyMotionHint,
                _clock.GetElapsedTime(now).TotalMilliseconds);
        }
        catch { /* 只借用已有帧的诊断不能改变导航、恢复或取消结果。 */ }
    }

    internal static void SwimGate(Func<ILogger> getLogger, string? route, string point, string move,
        string phase, string reason, int before, int after, CaptureFrameStamp previous,
        PathMoveObservation? observation, CaptureFrameFence? flightFence, CaptureFrameFence? dashFence)
    {
        if (before == after) return;
        try
        {
            var sink = getLogger();
            if (!sink.IsEnabled(LogLevel.Debug)) return;
            sink.LogDebug("PATH_SWIM_GATE route={Route} point={Point} move={Move} event={Event} reason={Reason} before={Before} after={After} hasObservation={HasObservation} sourceSession={Session} sourceSequence={Sequence} sourceCaptured={Captured} sourceFrequency={Frequency} previousSession={PreviousSession} previousSequence={PreviousSequence} motion={Motion} valid={Valid} sourceUsable={SourceUsable} position={Position} flightFenceSession={FlightSession} flightFenceSource={FlightSource} flightInputCompleted={FlightCompleted} dashFenceSession={DashSession} dashFenceSource={DashSource} dashInputCompleted={DashCompleted}",
                route, point, move, phase, reason, before, after, observation.HasValue,
                observation?.Stamp.SessionId, observation?.Stamp.Sequence, observation?.Stamp.CapturedTimestamp,
                observation?.Stamp.TimestampFrequency, previous.SessionId, previous.Sequence,
                observation?.Motion, observation?.Valid, observation?.SourceUsable, observation?.Position,
                flightFence?.Before.SessionId, flightFence?.Before.Sequence, flightFence?.InputCompletedTimestamp,
                dashFence?.Before.SessionId, dashFence?.Before.Sequence, dashFence?.InputCompletedTimestamp);
        }
        catch { /* 记录真实计数变化，诊断失败不能改变原门禁与异常。 */ }
    }
}
