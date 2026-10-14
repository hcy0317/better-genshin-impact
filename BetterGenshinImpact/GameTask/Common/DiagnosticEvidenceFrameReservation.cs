using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.Common;

internal readonly record struct DiagnosticFrameReservationKey(Guid Battle, Guid Request, long Epoch);
internal enum DiagnosticFrameReservationState { Pending, Fulfilled, Claimed, Released, Expired, Cancelled, Rejected }

// These handles deliberately expose no ImageRegion, Mat, or writable pixel callback.
internal sealed class DiagnosticFrameReservation(DiagnosticEvidenceScope scope,
    DiagnosticFrameReservationKey key) : IDisposable
{
    internal DiagnosticFrameReservationKey Key { get; } = key;
    internal DiagnosticFrameReservationState LastState = DiagnosticFrameReservationState.Pending;
    internal string? FailureReason { get; set; }
    internal bool SaveAccepted { get; set; }
    internal DiagnosticFrameReservationState State => scope.ReadReservationState(this);
    public void Dispose() => scope.ReleaseExactReservation(this);
}

internal sealed class DiagnosticExactFrameLease(DiagnosticEvidenceScope scope,
    DiagnosticFrameReservation reservation, CaptureFrameStamp source) : IDisposable
{
    internal DiagnosticFrameReservation Reservation { get; } = reservation;
    internal CaptureFrameStamp Source { get; } = source;
    internal bool Released { get; private set; }
    public void Dispose() => scope.ReleaseExactLease(this);
    internal void MarkReleased() => Released = true; // Only called under the owning scope lock.
}

internal sealed partial class DiagnosticEvidenceScope
{
    private sealed class ReservedFrame(CaptureFrameStamp after, TimeProvider clock, long deadline,
        CancellationToken cancellation)
    {
        internal readonly CaptureFrameStamp After = after;
        internal readonly TimeProvider Clock = clock;
        internal readonly long Deadline = deadline;
        internal readonly CancellationToken Cancellation = cancellation;
        internal ImageRegion? Frame;
        internal long Bytes;
        internal int References = 1; // Reservation ownership transfers to the single lease.
        internal bool Claimed;
        internal bool ReservationReleased;
        internal bool Saved;
        internal CancellationTokenRegistration Registration;
        internal bool Expired => Cancellation.IsCancellationRequested || Clock.GetTimestamp() >= Deadline;
    }

    private readonly Dictionary<DiagnosticFrameReservation, ReservedFrame> _reservations = new();
    private long _savedReservationBytes, _reservationRetained, _reservationReleased;
    internal (int Active, long Retained, long Released) ExactReservationStatistics
    { get { lock (_gate) return (_reservations.Count, _reservationRetained, _reservationReleased); } }

    internal DiagnosticFrameReservationState ReadReservationState(DiagnosticFrameReservation token)
    {
        lock (_gate)
        {
            if (_reservations.TryGetValue(token, out var state) && !state.Claimed)
            {
                if (state.Cancellation.IsCancellationRequested) FailReservation(token, state, DiagnosticFrameReservationState.Cancelled, "cancellation");
                else if (state.Clock.GetTimestamp() >= state.Deadline) FailReservation(token, state, DiagnosticFrameReservationState.Expired, "deadline");
            }
            return token.LastState;
        }
    }

    private void FailReservation(DiagnosticFrameReservation token, ReservedFrame state, DiagnosticFrameReservationState terminal, string reason)
    {
        if (token.FailureReason != null) return;
        token.LastState = terminal;
        token.FailureReason = "evidence-unavailable:" + reason;
        MissingFrame(token.Key.Request.ToString("N"), "last-observation", token.FailureReason, _logger);
        state.ReservationReleased = true;
        if (!state.Claimed) ReleaseExactReference(token);
    }

    internal DiagnosticFrameReservation? TryReserveExactFrame(DiagnosticFrameReservationKey key,
        CaptureFrameStamp after, TimeProvider clock, long deadline, CancellationToken cancellation)
        => TryReserveExactFrame(key, after, clock, deadline, cancellation, out _);

    internal DiagnosticFrameReservation? TryReserveExactFrame(DiagnosticFrameReservationKey key,
        CaptureFrameStamp after, TimeProvider clock, long deadline, CancellationToken cancellation, out string? rejectionReason)
    {
        lock (_gate)
        {
            foreach (var expired in _reservations.Keys.ToArray()) ReadReservationState(expired);
            var reason = _closed ? "closed" : key.Battle == Guid.Empty || key.Request == Guid.Empty ? "invalid-key" :
                !after.IsKnown ? "source-unknown" : clock.TimestampFrequency != after.TimestampFrequency ? "clock-mismatch" :
                cancellation.IsCancellationRequested ? "cancellation" : deadline <= clock.GetTimestamp() ? "deadline" :
                _reservations.Keys.Any(item => item.Key == key) ? "duplicate-request" :
                _exactFrames.Count + _reservations.Count >= 2 ? "exact-slots-full" : null;
            rejectionReason = reason == null ? null : "evidence-unavailable:" + reason;
            if (reason != null) return null; // Caller emits this single, request-bound rejection.
            var token = new DiagnosticFrameReservation(this, key);
            var state = new ReservedFrame(after, clock, deadline, cancellation);
            _reservations.Add(token, state);
            state.Registration = cancellation.Register(() =>
            {
                lock (_gate)
                    if (_reservations.TryGetValue(token, out var current) && !current.Claimed)
                        FailReservation(token, current, DiagnosticFrameReservationState.Cancelled, "cancellation");
            });
            if (!_reservations.ContainsKey(token)) state.Registration.Unregister();
            return token;
        }
    }

    internal bool TryFulfillExactFrame(DiagnosticFrameReservation token, ImageRegion frame)
    {
        lock (_gate)
        {
            ReadReservationState(token);
            if (!_reservations.TryGetValue(token, out var state) || state.Frame != null || state.ReservationReleased) return false;
            if (_closed) { FailReservation(token, state, DiagnosticFrameReservationState.Rejected, "closed"); return false; }
            if (!frame.FrameStamp.IsAfter(state.After) ||
                !frame.FrameStamp.IsFresh(state.Clock, TimeSpan.FromMilliseconds(150)))
            { FailReservation(token, state, DiagnosticFrameReservationState.Rejected, "stale-or-foreign-source"); return false; }
            try
            {
                if (frame.SrcMat.Empty()) { FailReservation(token, state, DiagnosticFrameReservationState.Rejected, "empty-frame"); return false; }
                var bytes = checked(frame.SrcMat.Total() * frame.SrcMat.ElemSize());
                if (!FitsByteLimit(bytes) || !FitsMemory(bytes))
                { FailReservation(token, state, DiagnosticFrameReservationState.Rejected, "exact-memory-or-byte-budget"); return false; }
                state.Frame = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = frame.FrameStamp };
                state.Bytes = bytes;
                _stagedBytes += bytes;
                _reservationRetained++;
                token.LastState = DiagnosticFrameReservationState.Fulfilled;
                return true;
            }
            catch
            { FailReservation(token, state, DiagnosticFrameReservationState.Rejected, "exact-retention-failed"); return false; }
        }
    }

    internal DiagnosticExactFrameLease? TryAcquireExactFrame(DiagnosticFrameReservation token)
    {
        lock (_gate)
        {
            ReadReservationState(token);
            if (_closed || !_reservations.TryGetValue(token, out var state) || state.Claimed ||
                state.ReservationReleased || state.Expired || state.Frame == null) return null;
            state.Claimed = true;
            token.LastState = DiagnosticFrameReservationState.Claimed;
            return new(this, token, state.Frame.FrameStamp);
        }
    }

    internal bool SaveExactFrame(DiagnosticExactFrameLease lease, DiagnosticFrameReservationKey key,
        CaptureFrameStamp expected, string phase, string detail)
    {
        lock (_gate)
        {
            // A previously admitted writer still owns the pixels; a duplicate is not missing evidence.
            if (lease.Reservation.SaveAccepted) return false;
            if (key != lease.Reservation.Key || lease.Source != expected)
                return MissingExactSave(lease.Reservation, "terminal-identity-mismatch");
            if (_closed) return MissingExactSave(lease.Reservation, "closed-before-terminal-save");
            if (lease.Released || !_reservations.TryGetValue(lease.Reservation, out var state) || !state.Claimed ||
                state.Frame == null || state.Frame.FrameStamp != expected)
                return MissingExactSave(lease.Reservation, "terminal-lease-unavailable");
            if (EvidenceImageLimitReached(DiagnosticEvidencePriority.Error))
                return MissingExactSave(lease.Reservation, "terminal-image-budget");
            if (!FitsByteLimit(state.Bytes, state.Bytes, terminal: true))
                return MissingExactSave(lease.Reservation, "terminal-byte-budget");
            ReclaimQueuedLowerPriority(0, DiagnosticEvidencePriority.Error);
            if (_queue.Count >= _queueCapacity) return MissingExactSave(lease.Reservation, "terminal-queue-full");
            var evidence = new DiagnosticEvidence(_runId, _accepted + 1, key.Request.ToString("N"),
                phase.Length > 64 ? phase[..64] : phase, expected,
                detail.Length > 2048 ? detail[..2048] : detail, Priority: DiagnosticEvidencePriority.Error);
            state.References++; // Writer borrows the same immutable pixels, without a second Clone.
            state.Saved = true;
            lease.Reservation.SaveAccepted = true;
            _savedReservationBytes += state.Bytes;
            _queue.AddLast((evidence, state.Frame.SrcMat, 0, () => ReleaseExactReference(lease.Reservation)));
            _accepted++;
            _bytes += state.Bytes;
            _queueWake.Writer.TryWrite(0);
            return true;
        }
    }

    private bool MissingExactSave(DiagnosticFrameReservation token, string reason)
    {
        if (token.FailureReason == null)
        {
            token.FailureReason = "evidence-unavailable:" + reason;
            MissingFrame(token.Key.Request.ToString("N"), "host-input-terminal", token.FailureReason, _logger);
        }
        return false;
    }

    internal void ReleaseExactReservation(DiagnosticFrameReservation token)
    {
        lock (_gate)
        {
            ReadReservationState(token);
            if (!_reservations.TryGetValue(token, out var state) || state.ReservationReleased) return;
            if (!state.Claimed)
            { FailReservation(token, state, DiagnosticFrameReservationState.Rejected, _closed ? "closed" : "no-frame-before-release"); return; }
            state.ReservationReleased = true;
            if (!state.Claimed) ReleaseExactReference(token);
        }
    }

    internal void ReleaseExactLease(DiagnosticExactFrameLease lease)
    {
        lock (_gate)
        {
            if (lease.Released || !_reservations.ContainsKey(lease.Reservation)) return;
            lease.MarkReleased();
            ReleaseExactReference(lease.Reservation);
        }
    }

    private void ReleaseExactReference(DiagnosticFrameReservation token)
    {
        lock (_gate)
        {
            if (!_reservations.TryGetValue(token, out var state) || --state.References != 0) return;
            _reservations.Remove(token);
            _stagedBytes -= state.Bytes;
            if (state.Saved) _savedReservationBytes -= state.Bytes;
            state.Registration.Unregister();
            if (state.Claimed || token.FailureReason == null) token.LastState = DiagnosticFrameReservationState.Released;
            if (state.Frame != null) _reservationReleased++;
            state.Frame?.Dispose();
        }
    }

    private void CloseExactReservations()
    {
        foreach (var token in _reservations.Keys.ToArray())
        {
            var state = _reservations[token];
            if (state.Claimed && !state.Saved)
                MissingExactSave(token, "closed-before-terminal-save");
            ReleaseExactReservation(token);
        }
    }
}
