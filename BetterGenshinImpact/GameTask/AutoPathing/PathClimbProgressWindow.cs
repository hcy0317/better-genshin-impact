using System;
using System.Collections.Generic;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal sealed class PathClimbProgressWindow
{
    private readonly Queue<Point2f> _positions = new();
    private DateTime? _sampledAt;
    private CaptureFrameStamp _previous;

    internal void Clear()
    {
        _positions.Clear();
        _sampledAt = null;
        _previous = default;
    }

    internal bool Observe(PathMoveObservation observation, DateTime now, int additionalTimeInMs)
    {
        // Polling may outpace capture. The same usable producer frame is neither
        // another sample nor a break in the already-observed Normal sequence.
        if (observation.SourceUsable && observation.Motion == MotionStatus.Normal &&
            _previous.IsKnown && observation.Stamp == _previous)
            return false;
        if (!observation.Valid || observation.Motion != MotionStatus.Normal ||
            _previous.IsKnown && !observation.Stamp.IsAfter(_previous))
        {
            Clear();
            return false;
        }
        _previous = observation.Stamp;
        if (_sampledAt == null) { _sampledAt = now; return false; }
        if ((now - _sampledAt.Value).TotalMilliseconds <= 1000 + additionalTimeInMs) return false;
        _sampledAt = now;
        _positions.Enqueue(observation.Position);
        if (_positions.Count < 9) return false;
        if (_positions.Count > 9) _positions.Dequeue();
        var positions = _positions.ToArray();
        // Same ninth-vs-second (latest-vs-^8) L1 comparison as ordinary movement.
        var delta = positions[^1] - positions[^8];
        return Math.Abs(delta.X) + Math.Abs(delta.Y) < 3;
    }
}
