using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;

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
    private readonly HashSet<GIActions> _held = new();
    private CaptureFrameStamp _previous;
    private long? _movementStarted;
    private long? _movementIdleStarted;

    internal PathRecoveryScope(PathMoveToIo io, UiOperation operation, CancellationToken ct,
        CaptureFrameStamp previous, Func<Task<PathMoveObservation>> observe)
    {
        Io = io;
        _operation = operation;
        _ct = ct;
        _previous = previous;
        _observe = observe;
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
            var observation = await _observe();
            Check();
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
        Io.Send(action, type);
        if (type == KeyType.KeyUp) _held.Remove(action);
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
            try { Io.Send(action, KeyType.KeyUp); }
            catch (Exception error) { failure ??= error; }
        }
        _held.Clear();
        if (failure != null) throw failure;
    }
}
