using System;

namespace BetterGenshinImpact.Core.Recognition.ONNX;

internal sealed class OnnxInitializationProgress
{
    private readonly TimeSpan _logInterval;
    private long _lastLoggedInterval;

    internal OnnxInitializationProgress(TimeSpan? logInterval = null)
    {
        _logInterval = logInterval ?? TimeSpan.FromSeconds(15);
        if (_logInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(logInterval), logInterval, "Log interval must be positive.");
        }
    }

    internal Observation Observe(TimeSpan elapsed)
    {
        var elapsedSeconds = Math.Max(0, (int)elapsed.TotalSeconds);
        var interval = elapsed.Ticks <= 0 ? 0 : elapsed.Ticks / _logInterval.Ticks;
        var shouldLog = interval > 0 && interval > _lastLoggedInterval;
        if (shouldLog)
        {
            _lastLoggedInterval = interval;
        }

        return new Observation(elapsedSeconds, shouldLog);
    }

    internal readonly record struct Observation(int ElapsedSeconds, bool ShouldLog);
}
