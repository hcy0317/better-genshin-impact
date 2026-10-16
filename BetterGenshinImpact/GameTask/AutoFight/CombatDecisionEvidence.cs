using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;

namespace BetterGenshinImpact.GameTask.AutoFight;

// Metadata handoff only. Pixel ownership and both frame slots remain in the run scope.
internal sealed class CombatDecisionEvidence : IDisposable
{
    private static readonly ConditionalWeakTable<DiagnosticEvidenceScope, List<CombatDecisionEvidence>> Pending = new();
    private readonly object _gate = new();
    private readonly DiagnosticEvidenceScope _scope;
    private readonly DiagnosticFrameReservation _reservation;
    private readonly List<CombatDecisionEvidence> _pending;
    private DiagnosticExactFrameLease? _lease;
    private PassiveTargetObservation _observation;
    private bool _disposed;
    internal string? FailureReason { get { _ = _reservation.State; return _reservation.FailureReason; } }
    internal bool SaveAccepted => _reservation.SaveAccepted;

    private CombatDecisionEvidence(DiagnosticEvidenceScope scope, DiagnosticFrameReservation reservation,
        List<CombatDecisionEvidence> pending)
    { _scope = scope; _reservation = reservation; _pending = pending; }

    internal static CombatDecisionEvidence? TryReserve(DiagnosticEvidenceScope scope, Guid battle, long epoch,
        CombatBattleHostInput input, TimeProvider clock, CancellationToken ct)
        => TryReserve(scope, battle, epoch, input, clock, ct, out _);

    internal static CombatDecisionEvidence? TryReserve(DiagnosticEvidenceScope scope, Guid battle, long epoch,
        CombatBattleHostInput input, TimeProvider clock, CancellationToken ct, out string? rejectionReason)
    {
        var token = scope.TryReserveExactFrame(new(battle, input.RequestId, epoch), input.Source, clock, input.DeadlineTimestamp, ct, out rejectionReason);
        if (token == null) return null;
        var pending = Pending.GetValue(scope, _ => new());
        var result = new CombatDecisionEvidence(scope, token, pending);
        lock (pending) pending.Add(result);
        return result;
    }

    internal static void Publish(DiagnosticEvidenceScope? scope, ImageRegion image, PassiveTargetObservation observation)
    {
        if (scope != null)
            try { CombatInputFeedbackEvidence.Publish(scope, image, observation); } catch { }
        if (scope == null || !Pending.TryGetValue(scope, out var pending)) return;
        CombatDecisionEvidence[] snapshot;
        lock (pending) snapshot = pending.ToArray();
        foreach (var item in snapshot) item.Offer(image, observation);
    }

    private void Offer(ImageRegion image, PassiveTargetObservation observation)
    {
        lock (_gate)
        {
            if (_disposed || _lease != null || observation.Source != image.FrameStamp ||
                observation.ImageWidth != image.Width || observation.ImageHeight != image.Height ||
                observation.BattleId != _reservation.Key.Battle || observation.CaptureEpoch != _reservation.Key.Epoch ||
                observation.Quality != CombatObservationQuality.Available) return;
            if (!_scope.TryFulfillExactFrame(_reservation, image)) return;
            _lease = _scope.TryAcquireExactFrame(_reservation);
            if (_lease != null) _observation = observation;
        }
    }

    internal bool TryRead(out PassiveTargetObservation observation)
    {
        lock (_gate)
        { observation = _observation; return !_disposed && _lease != null; }
    }

    internal bool Save(string detail)
    {
        lock (_gate)
            return !_disposed && _lease != null && _scope.SaveExactFrame(_lease, _reservation.Key,
                _observation.Source, "host-input-terminal", detail);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lease?.Dispose();
            _reservation.Dispose();
        }
        lock (_pending) _pending.Remove(this);
    }
}
