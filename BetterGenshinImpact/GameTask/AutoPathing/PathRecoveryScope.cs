using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.Common;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal sealed class RecoveryObservationChanged : Exception { }
internal sealed class RecoveryMovementExpired : Exception { }

// Lives inside one UiOperation carrying the original node's remaining budget.
internal sealed class PathRecoveryScope : IDisposable
{
    internal PathMoveToIo Io { get; }
    private readonly UiOperation _operation;
    private readonly CancellationToken _ct;
    private readonly Func<Task<PathMoveObservation>> _observe;
    private readonly Action<GIActions, KeyType, DiagnosticInputReceipt, string>? _inputTrace;
    private readonly HashSet<GIActions> _held = new();
    private CaptureFrameStamp _previous;
    private long? _movementStarted;
    private long? _movementIdleStarted;
    private long? _lastInputAt;
    private CaptureFrameFence? _inputFence;

    internal PathRecoveryScope(PathMoveToIo io, UiOperation operation, CancellationToken ct,
        CaptureFrameStamp previous, Func<Task<PathMoveObservation>> observe,
        Action<GIActions, KeyType, DiagnosticInputReceipt, string>? inputTrace = null)
    {
        Io = io;
        _operation = operation;
        _ct = ct;
        _previous = previous;
        _observe = observe;
        _inputTrace = inputTrace;
    }

    internal void Check()
    {
        // Parent deadline/cancellation wins; it must never be swallowed as a normal inner-loop exit.
        _ct.ThrowIfCancellationRequested();
        _operation.Check();
        if (_movementStarted is { } started && Io.Clock.GetElapsedTime(started) >= TimeSpan.FromSeconds(25) ||
            _movementIdleStarted is { } idle && Io.Clock.GetElapsedTime(idle) >= TimeSpan.FromSeconds(5))
            throw new RecoveryMovementExpired();
    }

    internal void BeginMovement()
    {
        Check();
        _movementStarted = Io.Clock.GetTimestamp();
        _movementIdleStarted = null;
    }

    internal void BeginMovementIdleWindow()
    {
        Check();
        _movementIdleStarted = Io.Clock.GetTimestamp();
    }

    internal void EndMovement()
    {
        _movementStarted = null;
        _movementIdleStarted = null;
    }

    internal async Task<PathMoveObservation> ObserveAsync()
    {
        while (true)
        {
            Check();
            if (_lastInputAt is { } inputAt && Io.Clock.GetElapsedTime(inputAt).TotalMilliseconds < 60)
            {
                await Io.Delay((int)Math.Ceiling(60 - Io.Clock.GetElapsedTime(inputAt).TotalMilliseconds), _ct);
                Check();
            }
            var observation = await _observe();
            Check();
            if (observation.SourceUsable && _inputFence is { } fence && !fence.Accepts(observation.Stamp))
            {
                // 有按住的移动键时不能把失效反馈等待变成长按；原清理路径立即松键。
                if (_held.Count != 0 || observation.Stamp.SessionId != fence.Before.SessionId)
                    throw new RecoveryObservationChanged();
                await Io.Delay(50, _ct);
                Check();
                continue;
            }
            if (observation.SourceUsable && observation.Stamp == _previous && observation.Motion == MotionStatus.Normal)
            {
                // A duplicate cannot authorize input. Wait within the SAME operation; stale/changed data aborts below.
                await Io.Delay(50, _ct);
                Check();
                continue;
            }
            if (!observation.Valid || observation.Motion != MotionStatus.Normal || !observation.Stamp.IsAfter(_previous))
                throw new RecoveryObservationChanged();
            _previous = observation.Stamp;
            return observation;
        }
    }

    internal async Task BeforeInputAsync()
    {
        Check();
        Io.CheckInput();
        Check();
        await ObserveAsync();
        Check();
    }

    internal async Task SendAsync(GIActions action, KeyType type = KeyType.KeyPress)
    {
        if (type == KeyType.KeyUp && action != GIActions.MoveForward && !_held.Contains(action)) return;
        if (type != KeyType.KeyUp) await BeforeInputAsync();
        // Track before dispatch: an exception may still have partially pressed the key.
        if (type == KeyType.KeyDown) _held.Add(action);
        SendObserved(action, type);
        if (type == KeyType.KeyUp) _held.Remove(action);
    }

    private void SendObserved(GIActions action, KeyType type)
    {
        if (_inputTrace == null)
        {
            try { Io.Send(action, type); }
            finally { RecordInputFence(); }
        }
        else
        {
            using var input = new DiagnosticInputAttempt(Io.Clock);
            Exception? failure = null;
            var environment = "not-dispatched";
            var sampled = false;
            using var capture = new InputDispatchCapture(() =>
            {
                if (sampled) return;
                sampled = true;
                try { environment = Io.InputEnvironment?.Invoke(action) ?? "not-exposed-by-io"; }
                catch { environment = "unknown:observation-failed"; }
            });
            try { Io.Send(action, type); }
            catch (Exception error) { failure = error; throw; }
            finally
            {
                RecordInputFence();
                try { _inputTrace(action, type, input.Complete(failure == null, failure), environment); }
                catch { /* 诊断不能替换输入异常或跳过松键。 */ }
            }
        }
    }

    private void RecordInputFence()
    {
        _lastInputAt = Io.Clock.GetTimestamp();
        _inputFence = new(_previous, _lastInputAt.Value);
    }

    internal async Task DelayAsync(int milliseconds)
    {
        var until = Io.Clock.GetTimestamp();
        while (Io.Clock.GetElapsedTime(until).TotalMilliseconds < milliseconds)
        {
            Check();
            var remaining = milliseconds - Io.Clock.GetElapsedTime(until).TotalMilliseconds;
            await Io.Delay((int)Math.Ceiling(Math.Min(50, remaining)), _ct);
            Check();
            await ObserveAsync();
        }
    }

    public void Dispose()
    {
        Exception? failure = null;
        foreach (var action in _held)
        {
            try { SendObserved(action, KeyType.KeyUp); }
            catch (Exception error) { failure ??= error; }
        }
        _held.Clear();
        if (failure != null) throw failure;
    }
}
