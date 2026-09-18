using System;
using System.Threading;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

/// <summary>生产/回放共用的选角协议。替身只提供帧、识别、物理输入和时钟。</summary>
internal static class AvatarSelectionProtocol
{
    internal enum Outcome { Awaiting, Ready, Unavailable, Unfulfilled, RetiredWithoutCompletion, UnconfirmedTerminal }

    internal sealed class Result<TFrame>(bool confirmed, bool needsRecovery, CaptureFrameStamp source,
        TFrame? frame, TFrame? before, CaptureFrameFence? inputFence, bool awaitingObservation = false,
        Outcome? outcome = null) : IDisposable where TFrame : class, IDisposable
    {
        private TFrame? _frame = frame, _before = before;
        internal Outcome State { get; } = outcome ?? (awaitingObservation ? Outcome.Awaiting
            : confirmed ? Outcome.Ready : Outcome.Unavailable);
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
        private readonly Func<int, CombatNativeInputRequest, CombatBattleHostInputResult> _select;
        private readonly TimeProvider _clock;
        private readonly TimeSpan _maximumAge;
        private readonly TimeSpan _duration;
        private readonly long _started;
        private readonly Func<TFrame, bool>? _canObserve;
        private readonly Action? _release;
        private readonly Action<TFrame, CombatNativeInputRequest>? _beforeSubmit;
        internal Guid GoalId { get; } = Guid.NewGuid();
        private Guid _requestId = Guid.NewGuid();
        private readonly long _deadline;
        private long _nextRequestAt;
        private long? _lastInput;
        private CaptureFrameStamp _last;
        private CaptureFrameFence? _fence;
        private TFrame? _before;
        private int _consecutive, _requests, _mismatchIndex, _consecutiveMismatch;
        private bool _uncertainSubmission, _closed;
        internal bool HasSubmittedInput => _lastInput != null;
        internal CaptureFrameStamp ObservedSource { get; private set; }
        internal int? ObservedIndex { get; private set; }
        internal string Reason { get; private set; } = "created";
        internal double ElapsedSeconds => _clock.GetElapsedTime(_started).TotalSeconds;
        internal bool HasUnconfirmedSubmission => _uncertainSubmission;
        internal bool RetirementRequested { get; private set; }
        internal void RequestRetirement() => RetirementRequested = true;
        internal long DeadlineTimestamp => _deadline;
        internal bool CanAssist => !_closed && !IsExpired && !RetirementRequested && HasSubmittedInput &&
            !_uncertainSubmission && _consecutiveMismatch >= 2;
        internal bool CanAssistFrom(CaptureFrameStamp source) => CanAssist &&
            source.IsFresh(_clock, _maximumAge) && _fence?.Accepts(source) == true;
        internal void ObserveAssistance(CaptureFrameStamp source, long completed)
        {
            // 新鲜度在原生输入前检查。这里记录已经发生的副作用，不能因物理等待把它丢弃。
            if (_closed || _uncertainSubmission || _fence?.Accepts(source) != true ||
                completed < source.CapturedTimestamp || completed > _clock.GetTimestamp())
                throw new InvalidOperationException("选角辅助运动不属于当前可交接的新帧");
            _fence = new(source, completed);
            _consecutive = _mismatchIndex = _consecutiveMismatch = 0;
        }
        internal bool IsClosed => _closed;
        internal bool IsExpired => _clock.GetElapsedTime(_started) >= _duration;

        internal Continuation(int expectedIndex, int maximumRequests, TimeSpan duration,
            Func<TFrame?> capture, Func<TFrame, CaptureFrameStamp> source, Func<TFrame, bool> isHud,
            Func<TFrame, int> active, Func<TFrame, TFrame> clone,
            Func<int, CombatNativeInputRequest, CombatBattleHostInputResult> select,
            TimeProvider clock, TimeSpan? maximumAge = null, Func<TFrame, bool>? canObserve = null, Action? release = null,
            Action<TFrame, CombatNativeInputRequest>? beforeSubmit = null)
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
            _canObserve = canObserve;
            _release = release;
            _beforeSubmit = beforeSubmit;
            _maximumAge = maximumAge ?? TimeSpan.FromMilliseconds(150);
            _started = clock.GetTimestamp();
            _deadline = checked(_started + (long)Math.Ceiling(duration.TotalSeconds * clock.TimestampFrequency));
        }

        internal Result<TFrame> Advance(CancellationToken ct, bool allowInput = true)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            ct.ThrowIfCancellationRequested();
            if (_clock.GetElapsedTime(_started) >= _duration) { Reason = "original-deadline"; return Finish(false, false, null); }
            if (RetirementRequested && !HasSubmittedInput) return Finish(false, false, null, Outcome.RetiredWithoutCompletion);
            TFrame? frame = null;
            try
            {
                frame = _capture();
                ct.ThrowIfCancellationRequested();
                if (frame == null) return Unknown("no-frame");
                var stamp = _source(frame);
                ObservedSource = stamp;
                if (!stamp.IsFresh(_clock, _maximumAge)) return Unknown("stale-or-unknown-source");
                if (_last.IsKnown && !stamp.IsAfter(_last)) return Unknown("duplicate-reordered-or-restarted-source");
                if (_fence is { } fence && !fence.Accepts(stamp)) return Unknown("pre-input-or-foreign-source");
                _last = stamp;
                if (!_isHud(frame))
                {
                    Reason = "hud-unavailable";
                    var result = Finish(false, true, frame);
                    frame = null;
                    return result;
                }
                if (_canObserve?.Invoke(frame) == false) return Unknown("control-not-ready-or-blocked");
                var observed = _active(frame);
                ObservedIndex = observed;
                ct.ThrowIfCancellationRequested();
                if (!stamp.IsFresh(_clock, _maximumAge)) return Unknown("recognition-late");
                if (observed > 0 && observed != _expectedIndex)
                {
                    _consecutiveMismatch = observed == _mismatchIndex ? Math.Min(2, _consecutiveMismatch + 1) : 1;
                    _mismatchIndex = observed;
                }
                else { _mismatchIndex = _consecutiveMismatch = 0; }
                _consecutive = AvatarSwitchConfirmationPolicy.Observe(_consecutive, observed, _expectedIndex);
                if (AvatarSwitchConfirmationPolicy.IsConfirmed(_consecutive))
                {
                    Reason = "target-confirmed";
                    var result = Finish(true, false, frame);
                    frame = null;
                    return result;
                }
                if (RetirementRequested && !_uncertainSubmission && _consecutiveMismatch >= 2)
                    return Finish(false, false, null, Outcome.RetiredWithoutCompletion);
                var canRetry = !_uncertainSubmission && _consecutiveMismatch >= 2 &&
                    _clock.GetTimestamp() >= _nextRequestAt;
                Reason = _uncertainSubmission ? "unknown-submission-observe-only" : RetirementRequested ? "retiring-without-completion"
                    : observed <= 0 ? "identity-unknown" : observed == _expectedIndex ? "awaiting-second-target-frame"
                    : canRetry ? "stable-mismatch-retry" : "awaiting-fresh-mismatch-or-throttle";
                if (allowInput && !RetirementRequested && observed > 0 && observed != _expectedIndex && _requests < _maximumRequests &&
                    (_lastInput == null || canRetry))
                {
                    _before?.Dispose();
                    _before = _clone(frame);
                    ct.ThrowIfCancellationRequested();
                    var request = new CombatNativeInputRequest(_requestId, stamp, _deadline);
                    _beforeSubmit?.Invoke(frame, request);
                    var receipt = _select(_expectedIndex, request);
                    if (receipt.Status is CombatBattleHostInputStatus.Sent or CombatBattleHostInputStatus.Unknown || receipt.NativeSubmitted > 0)
                    {
                        var completed = receipt.CompletedTimestamp ?? receipt.ObservableAfterTimestamp ?? _clock.GetTimestamp();
                        if (completed < stamp.CapturedTimestamp || completed > _clock.GetTimestamp())
                            throw new InvalidOperationException("切人输入回执的时间边界无效");
                        _lastInput = completed;
                        _fence = new(stamp, completed);
                        _requests++;
                        _uncertainSubmission |= receipt.Status != CombatBattleHostInputStatus.Sent ||
                            receipt.NativeRequested is { } requested && receipt.NativeSubmitted != requested;
                        // 这是同一目标的新物理脉冲；NotSent不会走到这里，也不会刷新原deadline。
                        _requestId = Guid.NewGuid();
                        var spacingMilliseconds = Math.Min(1000, 250 << Math.Min(_requests - 1, 2));
                        _nextRequestAt = completed + _clock.TimestampFrequency * spacingMilliseconds / 1000;
                        _mismatchIndex = _consecutiveMismatch = 0;
                    }
                    ct.ThrowIfCancellationRequested();
                    if (receipt.Status == CombatBattleHostInputStatus.Failed)
                        throw receipt.Error ?? new InvalidOperationException(receipt.Reason);
                }
                var waiting = new Result<TFrame>(false, false, _last, frame, null, _fence, awaitingObservation: true);
                frame = null;
                return waiting;
            }
            finally { frame?.Dispose(); }
        }

        private Result<TFrame> Unknown(string reason)
        {
            Reason = reason;
            ObservedIndex = null;
            _consecutive = 0;
            _mismatchIndex = _consecutiveMismatch = 0;
            return IsExpired ? Finish(false, false, null)
                : new(false, false, _last, null, null, _fence, awaitingObservation: true);
        }

        private Result<TFrame> Finish(bool confirmed, bool recovery, TFrame? frame, Outcome? terminal = null)
        {
            // 发布Ready/可交接前必须释放本目标所属按键；失败时目标保持打开，不能交给新持有者。
            if (confirmed || terminal == Outcome.RetiredWithoutCompletion) _release?.Invoke();
            var before = recovery && HasSubmittedInput ? _before : null;
            if (before != null) _before = null;
            Dispose();
            var outcome = terminal ?? (confirmed ? Outcome.Ready : _uncertainSubmission ? Outcome.UnconfirmedTerminal
                : HasSubmittedInput ? Outcome.Unfulfilled : Outcome.Unavailable);
            return new(confirmed, recovery, _last, frame, before, _fence, outcome: outcome);
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
        Func<TFrame, int> active, Func<TFrame, TFrame> clone,
        Func<int, CombatNativeInputRequest, CombatBattleHostInputResult> select,
        Action<int> wait, CancellationToken ct, TimeProvider? clock = null,
        TimeSpan? maximumAge = null, Action<int>? trace = null) where TFrame : class, IDisposable
    {
        clock ??= TimeProvider.System;
        using var continuation = new Continuation<TFrame>(expectedIndex, attempts,
            TimeSpan.FromMilliseconds(Math.Max(1, attempts) * 250d), capture, source, isHud,
            frame =>
            {
                var observed = active(frame);
                trace?.Invoke(observed);
                return observed;
            }, clone, select, clock, maximumAge);
        while (true)
        {
            var result = continuation.Advance(ct);
            if (!result.AwaitingObservation) return result;
            result.Dispose();
            wait(50);
        }
    }
}
