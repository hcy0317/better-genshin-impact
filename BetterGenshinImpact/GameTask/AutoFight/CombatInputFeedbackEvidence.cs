using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoFight;

// Metadata only until an existing producer frame arrives. No capture, wait,
// input permission, or additional exact-frame slot is owned by this collector.
internal static class CombatInputFeedbackEvidence
{
    private sealed record Request(Guid Battle, long Epoch, CombatBattleHostInput Input,
        CombatBattleHostInputResult Result, TimeProvider Clock, ILogger Logger);
    private sealed class Pending { internal readonly List<Request> Requests = new(); }
    private static readonly ConditionalWeakTable<DiagnosticEvidenceScope, Pending> Scopes = new();

    internal static void Arm(DiagnosticEvidenceScope? scope, Guid battle, long epoch,
        CombatBattleHostInput input, CombatBattleHostInputResult result, TimeProvider clock, ILogger logger)
    {
        if (scope == null || input.Kind is not (CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.Approach) ||
            !result.HasCompleteReceipt || result.NativeUncertain || result.Error != null || result.CompletedTimestamp == null) return;
        var pending = Scopes.GetValue(scope, _ => new());
        lock (pending)
        {
            // Each host serializes input. Keep metadata bounded even if a producer disappears.
            if (pending.Requests.Count == 2)
            { Missing(pending.Requests[0], "feedback-request-replaced"); pending.Requests.RemoveAt(0); }
            pending.Requests.Add(new(battle, epoch, input, result, clock, logger));
        }
    }

    internal static void Publish(DiagnosticEvidenceScope scope, ImageRegion image, PassiveTargetObservation observed)
    {
        if (!Scopes.TryGetValue(scope, out var pending)) return;
        lock (pending)
        {
            foreach (var request in pending.Requests.ToArray())
            {
                if (observed.BattleId != request.Battle) continue;
                if (request.Clock.GetTimestamp() >= request.Input.DeadlineTimestamp || observed.CaptureEpoch != request.Epoch ||
                    observed.Source.SessionId != request.Input.Source.SessionId)
                { Missing(request, "feedback-deadline-or-source-changed"); pending.Requests.Remove(request); continue; }
                if (observed.Source != image.FrameStamp || observed.ImageWidth != image.Width || observed.ImageHeight != image.Height ||
                    observed.Quality != CombatObservationQuality.Available || !observed.Source.IsAfter(request.Input.Source) ||
                    !observed.Source.IsFresh(request.Clock, TimeSpan.FromMilliseconds(150)) ||
                    observed.Source.TimestampFrequency != request.Clock.TimestampFrequency ||
                    observed.Source.CapturedTimestamp < (request.Result.ObservableAfterTimestamp ?? request.Result.CompletedTimestamp!.Value) +
                        request.Clock.TimestampFrequency * 60 / 1000) continue;
                pending.Requests.Remove(request); // One exact target observation; later frames are only context.
                scope.TryCapture(image, request.Input.RequestId.ToString("N"), "host-input-feedback",
                    "first fresh published feedback after input; not arrival, damage, obstacle or completion proof", request.Logger,
                    priority: DiagnosticEvidencePriority.Warning, fields: new Dictionary<string, string>
                    {
                        ["battle"] = request.Battle.ToString("N"),
                        ["inputSource"] = $"{request.Input.Source.SessionId}/{request.Input.Source.Sequence}",
                        ["inputIntent"] = $"kind={request.Input.Kind} x={request.Input.X} y={request.Input.Y}",
                        ["inputReceipt"] = request.Result.ToString(),
                        ["feedbackTarget"] = $"health={observed.HasNormalHealthBar} visual={observed.Visual} direction={observed.IndicatorDecision} damage={observed.HasDamageCue}",
                        ["feedbackControl"] = $"{observed.Control} motion={observed.Motion} epoch={observed.CaptureEpoch}"
                    });
                // Share sparse first/change/last context; every exact feedback above keeps its own receipt.
                // Source ids, timestamps and pixel geometry are deliberately absent from the change key.
                var contextRequest = $"host-input:{request.Battle:N}:{request.Epoch}:{observed.Source.SessionId}";
                var changeKey = $"kind={request.Input.Kind} health={observed.HasNormalHealthBar} visual={observed.Visual != null} " +
                    $"direction={observed.IndicatorDecision?.Direction} damage={observed.HasDamageCue} " +
                    $"control={observed.Control.IsObserved}/{observed.Control.Motion}/{observed.Control.KeyboardBreakoutRequested} motion={observed.Motion}";
                scope.RequestWindowFromFrame(contextRequest, "host-input-context", image,
                    "existing producer context after input; anchor observation is not a per-frame hit, arrival or obstacle verdict",
                    request.Logger, fields: new Dictionary<string, string>
                    {
                        ["battle"] = request.Battle.ToString("N"),
                        ["inputSource"] = $"{request.Input.Source.SessionId}/{request.Input.Source.Sequence}",
                        ["inputIntent"] = $"kind={request.Input.Kind} x={request.Input.X} y={request.Input.Y}",
                        ["inputReceipt"] = request.Result.ToString(),
                        ["inputRequest"] = request.Input.RequestId.ToString("N"),
                        ["contextAnchorSource"] = $"{observed.Source.SessionId}/{observed.Source.Sequence}",
                        ["anchorFeedbackTarget"] = $"health={observed.HasNormalHealthBar} visual={observed.Visual} direction={observed.IndicatorDecision} damage={observed.HasDamageCue}",
                        ["anchorFeedbackControl"] = $"{observed.Control} motion={observed.Motion} epoch={observed.CaptureEpoch}"
                    }, changeKey: changeKey);
            }
        }
    }

    internal static void End(DiagnosticEvidenceScope? scope, Guid battle)
    {
        if (scope == null || !Scopes.TryGetValue(scope, out var pending)) return;
        lock (pending)
            foreach (var request in pending.Requests.ToArray())
                if (request.Battle == battle)
                { Missing(request, "feedback-not-published-before-host-release"); pending.Requests.Remove(request); }
    }

    private static void Missing(Request request, string reason)
    {
        try { request.Logger.LogDebug("EVIDENCE_CAPTURE_MISSING request={Request} phase=host-input-feedback reason={Reason}", request.Input.RequestId, reason); }
        catch { }
    }
}
