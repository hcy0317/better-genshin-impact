using System;
using System.Threading;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

/// <summary>生产/回放共用的选角协议。替身只提供帧、识别、物理输入和时钟。</summary>
internal static class AvatarSelectionProtocol
{
    private sealed class StopSelection : Exception;

    internal sealed class Result<TFrame>(bool confirmed, bool needsRecovery, CaptureFrameStamp source,
        TFrame? frame, TFrame? before, CaptureFrameFence? inputFence) : IDisposable where TFrame : class, IDisposable
    {
        private TFrame? _frame = frame, _before = before;
        internal bool Confirmed { get; } = confirmed;
        internal bool NeedsRecovery { get; } = needsRecovery;
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
