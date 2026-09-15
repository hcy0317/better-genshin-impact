using System;
using System.Threading;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

/// <summary>生产/回放共用的选角协议。替身只提供帧、识别、物理输入和时钟。</summary>
internal static class AvatarSelectionProtocol
{
    private sealed class StopSelection : Exception;

    internal sealed class Result<TFrame>(bool confirmed, bool needsRecovery, CaptureFrameStamp source,
        TFrame? frame, TFrame? before, CaptureFrameFence? inputFence, bool awaitingObservation = false) : IDisposable where TFrame : class, IDisposable
    {
        private TFrame? _frame = frame, _before = before;
        internal bool Confirmed { get; } = confirmed;
        internal bool NeedsRecovery { get; } = needsRecovery;
        internal bool AwaitingObservation { get; } = awaitingObservation;
        internal CaptureFrameStamp Source { get; } = source;
        internal CaptureFrameFence? InputFence { get; } = inputFence;
        internal TFrame? TakeFrame() => Interlocked.Exchange(ref _frame, null);
        internal TFrame? TakeBefore() => Interlocked.Exchange(ref _before, null);
        public void Dispose()
        {
            try { TakeFrame()?.Dispose(); }
            finally { TakeBefore()?.Dispose(); }
        }
    }

    /// <summary>每次至多消费一个源帧；物理切人等待由后续Step推进，不内联Sleep或脱管工作。</summary>
    internal sealed class Continuation<TFrame> : IDisposable where TFrame : class, IDisposable
    {
        private readonly int _expectedIndex;
        private readonly int _maximumRequests;
        private readonly Func<TFrame?> _capture;
        private readonly Func<TFrame, CaptureFrameStamp> _source;
        private readonly Func<TFrame, bool> _isHud;
        private readonly Func<TFrame, int> _active;
        private readonly Func<TFrame, TFrame> _clone;
        private readonly Action<int> _select;
        private readonly TimeProvider _clock;
        private readonly TimeSpan _maximumAge;
        private readonly TimeSpan _duration;
        private readonly long _started;
        private long _unknownSince;
        private long? _lastInput;
        private CaptureFrameStamp _last;
        private CaptureFrameFence? _fence;
        private TFrame? _before;
        private int _consecutive, _requests;
        private bool _submitted, _closed;
        internal bool HasSubmittedInput => _lastInput != null;
        internal bool IsClosed => _closed;

        internal Continuation(int expectedIndex, int maximumRequests, TimeSpan duration,
            Func<TFrame?> capture, Func<TFrame, CaptureFrameStamp> source, Func<TFrame, bool> isHud,
            Func<TFrame, int> active, Func<TFrame, TFrame> clone, Action<int> select,
            TimeProvider clock, TimeSpan? maximumAge = null)
        {
            _expectedIndex = expectedIndex;
            _maximumRequests = maximumRequests;
            _duration = duration;
            _capture = capture;
            _source = source;
            _isHud = isHud;
            _active = active;
            _clone = clone;
            _select = select;
            _clock = clock;
            _maximumAge = maximumAge ?? TimeSpan.FromMilliseconds(150);
            _started = _unknownSince = clock.GetTimestamp();
        }

        internal Result<TFrame> Advance(CancellationToken ct, bool allowInput = true)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            ct.ThrowIfCancellationRequested();
            if (_clock.GetElapsedTime(_started) >= _duration) return Finish(false, false, null);
            TFrame? frame = null;
            try
            {
                frame = _capture();
                ct.ThrowIfCancellationRequested();
                if (frame == null) return Unknown();
                var stamp = _source(frame);
                if (!stamp.IsFresh(_clock, _maximumAge) || _last.IsKnown && !stamp.IsAfter(_last) ||
                    _fence is { } fence && !fence.Accepts(stamp)) return Unknown();
                _last = stamp;
                if (!_isHud(frame))
                {
                    var result = Finish(false, true, frame);
                    frame = null;
                    return result;
                }
                var observed = _active(frame);
                ct.ThrowIfCancellationRequested();
                if (!stamp.IsFresh(_clock, _maximumAge)) return Unknown();
                if (observed > 0) _unknownSince = _clock.GetTimestamp();
                else if (_clock.GetElapsedTime(_unknownSince) >= TimeSpan.FromSeconds(1)) return Finish(false, false, null);
                _submitted = false;
                _consecutive = AvatarSwitchConfirmationPolicy.Observe(_consecutive, observed, _expectedIndex);
                if (AvatarSwitchConfirmationPolicy.IsConfirmed(_consecutive))
                {
                    var result = Finish(true, false, frame);
                    frame = null;
                    return result;
                }
                if (allowInput && observed != _expectedIndex && _requests < _maximumRequests &&
                    (_lastInput == null || _clock.GetElapsedTime(_lastInput.Value) >= TimeSpan.FromMilliseconds(250)))
                {
                    _before?.Dispose();
                    _before = _clone(frame);
                    ct.ThrowIfCancellationRequested();
                    _select(_expectedIndex);
                    _lastInput = _clock.GetTimestamp();
                    _fence = new(stamp, _lastInput.Value);
                    _submitted = true;
                    _requests++;
                }
                return new(false, false, _last, null, null, _fence, awaitingObservation: true);
            }
            finally { frame?.Dispose(); }
        }

        private Result<TFrame> Unknown()
        {
            _consecutive = 0;
            return _clock.GetElapsedTime(_unknownSince) >= TimeSpan.FromSeconds(1)
                ? Finish(false, false, null)
                : new(false, false, _last, null, null, _fence, awaitingObservation: true);
        }

        private Result<TFrame> Finish(bool confirmed, bool recovery, TFrame? frame)
        {
            var before = recovery && _submitted ? _before : null;
            if (before != null) _before = null;
            Dispose();
            return new(confirmed, recovery, _last, frame, before, _fence);
        }

        public void Dispose()
        {
            _closed = true;
            _before?.Dispose();
            _before = null;
        }
    }

    internal static Result<TFrame> Select<TFrame>(int expectedIndex, int attempts,
        Func<TFrame?> capture, Func<TFrame, CaptureFrameStamp> source, Func<TFrame, bool> isHud,
        Func<TFrame, int> active, Func<TFrame, TFrame> clone, Action<int> select,
        Action<int> wait, CancellationToken ct, TimeProvider? clock = null,
        TimeSpan? maximumAge = null, Func<int, int, bool>? onMismatch = null,
        Action<int>? trace = null) where TFrame : class, IDisposable
    {
        clock ??= TimeProvider.System;
        var age = maximumAge ?? TimeSpan.FromMilliseconds(150);
        TFrame? before = null, frame = null;
        CaptureFrameStamp last = default;
        CaptureFrameFence? fence = null;
        var submitted = false;
        var canSelect = false;
        var needsRecovery = false;
        var unknownSince = clock.GetTimestamp();
        int Unknown()
        {
            if (clock.GetElapsedTime(unknownSince) >= TimeSpan.FromSeconds(1)) throw new StopSelection();
            return -1;
        }
        Result<TFrame> Finish(bool confirmed)
        {
            var causalBefore = needsRecovery && submitted ? before : null;
            if (causalBefore != null) before = null;
            var result = new Result<TFrame>(confirmed, needsRecovery, last, frame, causalBefore, fence);
            frame = null;
            return result;
        }
        try
        {
            var confirmed = AvatarSwitchConfirmationPolicy.TryConfirm(expectedIndex, attempts, () =>
            {
                canSelect = false;
                frame?.Dispose();
                frame = capture();
                if (frame == null) return Unknown();
                var stamp = source(frame);
                if (!stamp.IsFresh(clock, age) || last.IsKnown && !stamp.IsAfter(last) ||
                    fence is { } input && !input.Accepts(stamp)) return Unknown();
                last = stamp;
                if (!isHud(frame))
                {
                    needsRecovery = true;
                    throw new StopSelection();
                }
                var observed = active(frame);
                canSelect = stamp.IsFresh(clock, age);
                if (!canSelect) return Unknown();
                if (observed > 0) unknownSince = clock.GetTimestamp();
                else Unknown();
                submitted = false;
                if (observed != expectedIndex)
                {
                    before?.Dispose();
                    before = clone(frame);
                }
                trace?.Invoke(observed);
                return observed;
            }, index =>
            {
                ct.ThrowIfCancellationRequested();
                if (!canSelect || before == null) return;
                select(index);
                fence = new(source(before), clock.GetTimestamp());
                submitted = true;
            }, wait, ct, (attempt, observed) =>
            {
                if (!canSelect || onMismatch?.Invoke(attempt, observed) != true) return;
                submitted = false;
                canSelect = false;
                before?.Dispose();
                before = null;
            });
            return Finish(confirmed);
        }
        catch (StopSelection) { return Finish(false); }
        finally { before?.Dispose(); frame?.Dispose(); }
    }
}
