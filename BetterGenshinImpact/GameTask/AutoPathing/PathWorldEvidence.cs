using System;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.GameTask.AutoPathing;

// 只借用当前等待循环的原帧；像素由现有证据环缓存拥有，不保存新的Mat引用。
internal sealed class PathWorldEvidence
{
    private readonly PathMoveToIo io;
    private readonly string _request = "path-world:" + Guid.NewGuid().ToString("N");
    private readonly RuntimeStallDiagnostics _stall;
    private bool _blocked;
    private long _count;
    private string _last = "not-observed";
    private string? _firstSaurian;
    private string? _lastSaurian;
    private int _layoutChanges;
    private CaptureFrameStamp _firstSource;

    internal PathWorldEvidence(PathMoveToIo io)
    {
        this.io = io;
        _stall = new(SafeLogger(io), "path-world", _request, io.Clock);
    }

    private static ILogger SafeLogger(PathMoveToIo io)
    { try { return io.Logger; } catch { return NullLogger.Instance; } }

    internal RuntimeStallDiagnostics.PhaseMeasurement Measure(string phase) => _stall.Measure(phase);

    internal void Observed(ImageRegion frame, WorldFrameKind kind, bool fresh, float? orientation, bool ready)
    {
        try
        {
            if (!ready) _count++;
            _last = FormattableString.Invariant($"world={kind} fresh={fresh} orientation={orientation?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "not-read"} ready={ready} source={frame.FrameStamp.SessionId}/{frame.FrameStamp.Sequence} blockedFrames={_count}");
            var evidence = DiagnosticEvidenceScope.Current;
            if (ready && !_blocked) return;
            evidence?.ObserveExistingFrame(frame);
            if (_blocked && !ready && _count % 30 == 0 && _layoutChanges < 3)
            {
                _lastSaurian = SaurianUiReader.Describe(frame);
                evidence?.TryCapture(frame, _request, "layout-followup-" + ++_layoutChanges,
                    _last, SafeLogger(io), fields: Fields());
            }
            if (_blocked || ready) return;
            _blocked = true;
            _firstSource = frame.FrameStamp;
            _firstSaurian = SaurianUiReader.Describe(frame);
            evidence?.RequestWindowFromFrame(_request, "path-world-blocked", frame, _last, SafeLogger(io), fields: Fields());
            SafeLogger(io).LogWarning("PATH_WORLD_EVIDENCE request={Request} {State} {Saurian}", _request, _last, _firstSaurian);
        }
        catch { }
    }

    private Dictionary<string, string> Fields() => new()
    {
        ["world:last"] = _last,
        ["world:firstSource"] = $"{_firstSource.SessionId}/{_firstSource.Sequence}",
        ["world:firstSaurian"] = _firstSaurian ?? "not-observed",
        ["world:lastSaurian"] = _lastSaurian ?? "not-observed"
    };

    internal void Recovered(ImageRegion frame)
    {
        if (!_blocked) return;
        try
        {
            DiagnosticEvidenceScope.Current?.ObserveExistingFrame(frame);
            DiagnosticEvidenceScope.Current?.TryCapture(frame, _request, "path-world-recovered",
                "original world/compass/freshness checks passed; " + _last, SafeLogger(io), fields: Fields());
        }
        catch { }
    }

    internal void Failed(ImageRegion? frame, Exception error)
    {
        if (error is OperationCanceledException) return;
        try
        {
            var fields = Fields();
            var failure = error.GetType().Name + ":" + error.Message;
            fields["world:failure"] = failure.Length > 512 ? failure[..512] : failure;
            SafeLogger(io).LogWarning("PATH_WORLD_END request={Request} evidence={Evidence}", _request,
                Newtonsoft.Json.JsonConvert.SerializeObject(fields));
            var evidence = DiagnosticEvidenceScope.Current;
            if (frame != null)
            {
                evidence?.ObserveExistingFrame(frame);
                evidence?.TryCapture(frame, _request, "path-world-deadline", "existing failure decision frame; " + _last,
                    SafeLogger(io), priority: DiagnosticEvidencePriority.Error, fields: fields);
            }
            else evidence?.RequestLatestWindow(_request, "path-world-deadline",
                "no current frame; latest retained existing source, not a new failure-time capture; " + _last, SafeLogger(io), fields);
        }
        catch { }
    }
}
