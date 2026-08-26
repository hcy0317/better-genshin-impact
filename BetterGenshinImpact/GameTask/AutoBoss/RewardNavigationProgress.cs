using System;

namespace BetterGenshinImpact.GameTask.AutoBoss;

internal sealed class RewardNavigationProgress
{
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _heartbeatInterval;
    private TimeSpan _nextHeartbeatAt;

    internal RewardNavigationProgress(TimeSpan timeout, TimeSpan heartbeatInterval)
    {
        _timeout = timeout;
        _heartbeatInterval = heartbeatInterval;
        _nextHeartbeatAt = heartbeatInterval;
    }

    internal bool IsTimedOut(TimeSpan elapsed) => elapsed >= _timeout;

    internal bool ShouldLogMissing(TimeSpan elapsed)
    {
        if (elapsed < _nextHeartbeatAt)
        {
            return false;
        }

        _nextHeartbeatAt = elapsed + _heartbeatInterval;
        return true;
    }
}
