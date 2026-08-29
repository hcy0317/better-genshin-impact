using System;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal sealed class ReturnMainUiPassiveRecoveryPolicy
{
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _heartbeatInterval;
    private TimeSpan _nextHeartbeat;

    public ReturnMainUiPassiveRecoveryPolicy(TimeSpan timeout, TimeSpan heartbeatInterval)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(heartbeatInterval, TimeSpan.Zero);
        _timeout = timeout;
        _heartbeatInterval = heartbeatInterval;
        _nextHeartbeat = heartbeatInterval;
    }

    public bool IsTimedOut(TimeSpan elapsed)
    {
        return elapsed >= _timeout;
    }

    public bool ShouldLogHeartbeat(TimeSpan elapsed)
    {
        if (elapsed < _nextHeartbeat)
        {
            return false;
        }

        _nextHeartbeat += _heartbeatInterval;
        return true;
    }
}
