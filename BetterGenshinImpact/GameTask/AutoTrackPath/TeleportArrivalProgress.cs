using System;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal sealed class TeleportArrivalProgress(TimeProvider clock)
{
    private readonly long _started = clock.GetTimestamp();
    private CaptureFrameStamp _previous;
    private int _loadingFrames;
    private int _worldFrames;
    private CaptureFrameStamp _confirmedMapClosure;
    private long? _worldSince;
    internal bool MapClosureConfirmed => _confirmedMapClosure.IsKnown;
    internal bool LoadingObserved { get; private set; }
    internal bool Arrived { get; private set; }

    // The confirmation driver calls this only after a verified teleport input and two post-input map-closed frames.
    internal void ConfirmMapClosure(CaptureFrameStamp source)
    {
        if (!source.IsFresh(clock, UiSnapshot.RecoveryMaximumAge)) return;
        _confirmedMapClosure = _previous = source;
        _worldFrames = 0;
        _worldSince = null;
        Arrived = false;
    }

    internal string Describe(WorldFrameKind state) =>
        $"world={state},mapClosureConfirmed={MapClosureConfirmed},loadingObserved={LoadingObserved},stableWorldFrames={_worldFrames}";

    internal bool Observe(CaptureFrameStamp source, WorldFrameKind state)
    {
        if (!source.IsFresh(clock, UiSnapshot.RecoveryMaximumAge)) { ResetWorld(); return false; }
        if (_previous.IsKnown && source.SessionId != _previous.SessionId)
        {
            _previous = default;
            _confirmedMapClosure = default;
            _worldSince = null;
            _loadingFrames = _worldFrames = 0;
            LoadingObserved = Arrived = false;
        }
        if (_previous.IsKnown && !source.IsAfter(_previous)) { ResetWorld(); return false; }
        _previous = source;
        if (state == WorldFrameKind.Loading)
        {
            ResetWorld();
            if (++_loadingFrames >= 2) LoadingObserved = true;
        }
        else
        {
            _loadingFrames = 0;
            if (state == WorldFrameKind.Playable && source.IsAfter(_confirmedMapClosure))
            {
                _worldSince ??= clock.GetTimestamp();
                _worldFrames++;
            }
            else ResetWorld();
        }
        // Loading frames can be missed by polling or contain non-uniform artwork. They are diagnostic evidence,
        // not a prerequisite once the causal input/map-closure receipt and a stable world have been confirmed.
        Arrived = MapClosureConfirmed && _worldFrames >= 3 && _worldSince is { } since &&
            clock.GetElapsedTime(since) >= TimeSpan.FromMilliseconds(300) &&
            clock.GetElapsedTime(_started) >= TimeSpan.FromSeconds(1);
        return Arrived;
    }

    private void ResetWorld() { _worldFrames = 0; _worldSince = null; Arrived = false; }
}
