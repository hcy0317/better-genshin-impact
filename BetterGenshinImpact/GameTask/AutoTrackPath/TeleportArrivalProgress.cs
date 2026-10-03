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
    internal bool LoadingObserved { get; private set; }
    internal bool Arrived { get; private set; }

    internal bool Observe(CaptureFrameStamp source, WorldFrameKind state)
    {
        if (!source.IsFresh(clock, UiSnapshot.RecoveryMaximumAge)) { _worldFrames = 0; return false; }
        if (_previous.IsKnown && source.SessionId != _previous.SessionId)
        {
            _previous = default;
            _loadingFrames = _worldFrames = 0;
            LoadingObserved = Arrived = false;
        }
        if (_previous.IsKnown && !source.IsAfter(_previous)) { _worldFrames = 0; return false; }
        _previous = source;
        if (state == WorldFrameKind.Loading)
        {
            _worldFrames = 0;
            if (++_loadingFrames >= 2) LoadingObserved = true;
        }
        else
        {
            _loadingFrames = 0;
            _worldFrames = state == WorldFrameKind.Playable && LoadingObserved ? _worldFrames + 1 : 0;
        }
        Arrived = LoadingObserved && _worldFrames >= 3 && clock.GetElapsedTime(_started) >= TimeSpan.FromSeconds(1);
        return Arrived;
    }
}
