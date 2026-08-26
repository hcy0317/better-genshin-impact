using System;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal sealed class PathProgressHeartbeat
{
    private readonly TimeSpan _interval;
    private DateTime _nextReportAt;

    public PathProgressHeartbeat(DateTime startedAt, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), interval, "Heartbeat interval must be positive.");
        }

        _interval = interval;
        _nextReportAt = startedAt + interval;
    }

    public bool ShouldReport(DateTime now)
    {
        if (now < _nextReportAt)
        {
            return false;
        }

        do
        {
            _nextReportAt += _interval;
        } while (_nextReportAt <= now);

        return true;
    }
}

internal sealed class PathMovementWatchdog
{
    private readonly DateTime _startedAt;
    private readonly TimeSpan _climbTimeout;

    public PathMovementWatchdog(DateTime startedAt, TimeSpan climbTimeout)
    {
        if (climbTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(climbTimeout), climbTimeout, "Climb timeout must be positive.");
        }

        _startedAt = startedAt;
        _climbTimeout = climbTimeout;
    }

    public bool ShouldAbort(bool isClimbing, DateTime now)
    {
        return isClimbing && now - _startedAt >= _climbTimeout;
    }
}
