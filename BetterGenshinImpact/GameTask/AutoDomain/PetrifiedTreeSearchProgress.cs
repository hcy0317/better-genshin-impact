using System;

namespace BetterGenshinImpact.GameTask.AutoDomain;

internal sealed class PetrifiedTreeSearchProgress
{
    internal const int DefaultTimeoutSeconds = 90;
    internal const int MinTimeoutSeconds = 30;
    internal const int MaxTimeoutSeconds = 300;
    private const int LogIntervalSeconds = 15;

    private readonly int _timeoutSeconds;
    private int _lastLoggedInterval;

    internal PetrifiedTreeSearchProgress(int timeoutSeconds)
    {
        _timeoutSeconds = NormalizeTimeoutSeconds(timeoutSeconds);
    }

    internal int TimeoutSeconds => _timeoutSeconds;

    internal static int NormalizeTimeoutSeconds(int configured)
    {
        if (configured <= 0)
        {
            return DefaultTimeoutSeconds;
        }

        return Math.Clamp(configured, MinTimeoutSeconds, MaxTimeoutSeconds);
    }

    internal Observation Observe(TimeSpan elapsed)
    {
        var elapsedSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        var interval = elapsedSeconds / LogIntervalSeconds;
        var shouldLog = interval > 0 && interval > _lastLoggedInterval;
        if (shouldLog)
        {
            _lastLoggedInterval = interval;
        }

        return new Observation(
            elapsedSeconds,
            shouldLog,
            elapsed >= TimeSpan.FromSeconds(_timeoutSeconds));
    }

    internal readonly record struct Observation(int ElapsedSeconds, bool ShouldLog, bool TimedOut);
}
