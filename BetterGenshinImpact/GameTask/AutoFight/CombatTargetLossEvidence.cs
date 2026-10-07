using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>在发布已有识别结果时固定原帧；每场仅保留首次空目标、丢失与恢复事件。</summary>
internal sealed class CombatTargetLossEvidence(Guid battle, TimeProvider clock, ILogger logger) : IDisposable
{
    private readonly string _request = "perception:" + battle.ToString("N");
    private const string PositiveChannel = "combat-target-positive";
    private CaptureFrameStamp _lastSource, _lastTargetSource;
    private CaptureFrameStamp _lastCropSource;
    private bool _wasTarget, _lost, _emptyCaptured, _positiveCaptured, _returnedCaptured;

    internal void ObservePublished(ImageRegion frame, PassiveTargetObservation observed)
    {
        try
        {
            if (battle == Guid.Empty || observed.BattleId != battle || observed.Source != frame.FrameStamp ||
                !observed.Source.IsKnown || frame.SrcMat.Empty()) return;
            if (_lastSource.IsKnown && observed.Source.SessionId != _lastSource.SessionId)
            {
                DiagnosticEvidenceScope.Current?.ForgetBefore(PositiveChannel, _request);
                _wasTarget = false;
                _lastTargetSource = default;
                _lastCropSource = default;
            }
            else if (_lastSource.IsKnown && !observed.Source.IsAfter(_lastSource)) return;
            _lastSource = observed.Source;
            var evidence = DiagnosticEvidenceScope.Current;
            if (evidence == null) return;
            var target = observed.Quality == CombatObservationQuality.Available &&
                (observed.HasNormalHealthBar || observed.HasDamageCue || observed.IndicatorDecision?.Visual != null || observed.FixedTopHealth != null);
            var fields = new Dictionary<string, string>
            {
                ["battle"] = battle.ToString("N"),
                ["sourceRole"] = "published-perception-decision",
                ["sourceFresh"] = observed.Source.IsFresh(clock, TimeSpan.FromMilliseconds(150)).ToString(),
                ["captureEpoch"] = observed.CaptureEpoch.ToString(),
                ["quality"] = observed.Quality.ToString(),
                ["target"] = $"health={observed.HasNormalHealthBar} damage={observed.HasDamageCue} visual={observed.Visual} indicator={observed.IndicatorDecision} fixedHealth={observed.FixedTopHealth}",
                ["lastTargetSource"] = $"{_lastTargetSource.SessionId}/{_lastTargetSource.Sequence}",
                ["filterReasons"] = observed.Recognition == null ? "not-observed" : string.Join(";", observed.Recognition.Rejections.Take(6).Select(item => $"{item.Key}:{item.Value}")),
                ["recognitionCounts"] = observed.Recognition is { } counts ? $"raw={counts.RawComponents} accepted={counts.Accepted} widthRejected={counts.HealthWidthRejected} darkChecked={counts.DarkTrackChecked} darkAccepted={counts.DarkTrackAccepted}" : "not-observed",
                ["narrowTargets"] = observed.Recognition == null ? "not-observed" : string.Join(";", observed.Recognition.NarrowBarSamples.Take(6).Select(item => $"{item.X},{item.Y},{item.Width}x{item.Height},minW={item.MinimumWidth}")),
                ["darkTrack"] = observed.Recognition == null ? "not-observed" : string.Join(";", observed.Recognition.DarkTrackSamples.Take(6)),
                ["lastTargetCropSource"] = $"{_lastCropSource.SessionId}/{_lastCropSource.Sequence}",
                ["absenceReason"] = observed.TargetAbsenceReason ?? "not-observed",
                ["damageFallback"] = observed.DamageFallback ?? "not-observed",
                ["control"] = $"observed={observed.Control.IsObserved} motion={observed.Control.Motion} breakout={observed.Control.KeyboardBreakoutRequested}"
            };
            if (target)
            {
                if (!_positiveCaptured)
                {
                    _positiveCaptured = true;
                    evidence.TryCapture(frame, _request, "perception-target-present", "首次发布目标的实际原帧，不代表战斗完成", logger, fields: fields);
                }
                if (_lost && !_returnedCaptured)
                {
                    _returnedCaptured = true;
                    evidence.TryCapture(frame, _request, "perception-target-returned", "丢失后首次重新发布目标的实际原帧", logger, fields: fields);
                }
                // 最后目标仅保存有界小裁剪，保留原来源及全图坐标；不在每帧复制整图。
                var visual = observed.Visual ?? observed.IndicatorDecision?.Visual ?? observed.FixedTopHealth;
                if (!_lost && visual is { } region)
                {
                    var x = Math.Clamp(region.X - 16, 0, frame.Width);
                    var y = Math.Clamp(region.Y - 16, 0, frame.Height);
                    var right = Math.Clamp(region.X + region.Width + 16, x, frame.Width);
                    var bottom = Math.Clamp(region.Y + region.Height + 16, y, frame.Height);
                    if (right > x && bottom > y)
                    {
                        var bounds = new Rect(x, y, Math.Min(512, right - x), Math.Min(128, bottom - y));
                        using var crop = frame.DeriveCrop(bounds);
                        if (evidence.RememberBefore(crop, PositiveChannel, _request,
                            $"last-published-target-crop sourceRect={bounds} fullSize={frame.Width}x{frame.Height} cropClipped={right - x > 512 || bottom - y > 128}; {fields["target"]}"))
                            _lastCropSource = observed.Source;
                    }
                }
                _lastTargetSource = observed.Source;
            }
            else if (_wasTarget && !_lost)
            {
                _lost = true;
                evidence.CaptureFault(frame, PositiveChannel, _request, "perception-target-lost",
                    "首次从已发布目标变为无目标的实际原帧；前帧为最后目标裁剪，不是后来请求的下一帧", logger, fields: fields);
                evidence.RequestWindowFromFrame(_request, "perception-target-lost", frame,
                    "目标丢失时刻既有源帧窗口", logger, fields);
            }
            else if (!_positiveCaptured && !_emptyCaptured)
            {
                _emptyCaptured = true;
                evidence.TryCapture(frame, _request, "perception-empty-entry", "本场首次发布无目标的实际原帧", logger,
                    priority: DiagnosticEvidencePriority.Warning, fields: fields);
            }
            _wasTarget = target;
        }
        catch { /* 证据失败不影响发布结果、搜索或结束判定。 */ }
    }

    public void Dispose()
    {
        try { DiagnosticEvidenceScope.Current?.ForgetBefore(PositiveChannel, _request); } catch { }
    }
}
