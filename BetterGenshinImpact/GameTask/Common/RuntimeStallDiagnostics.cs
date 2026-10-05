using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common;

// 单循环所有者；不创建计时线程、不采图、不访问App或进程服务。只在恢复执行时报告观察到的停顿。
internal sealed class RuntimeStallDiagnostics(ILogger logger, string owner, string run,
    TimeProvider? clock = null, Func<TimeSpan>? gcPause = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<TimeSpan> _gcPause = gcPause ?? GC.GetTotalPauseDuration;
    private long? _previous;
    private TimeSpan _previousGcPause;
    private string? _previousPhase;
    private long _previousSource;

    internal PhaseMeasurement Measure(string phase)
    {
        try { return new(this, phase, _clock.GetTimestamp(), _gcPause()); }
        catch { return default; }
    }

    internal readonly struct PhaseMeasurement(RuntimeStallDiagnostics? owner, string phase, long started, TimeSpan pause) : IDisposable
    {
        public void Dispose() => owner?.Report(started, pause, "before-" + phase, "after-" + phase, 0, 0);
    }

    private void Report(long started, TimeSpan beforePause, string? beforePhase, string phase, long beforeSource, long sourceSequence)
    {
        try
        {
            var elapsed = _clock.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed < 2000) return;
            logger.LogWarning("RUNTIME_EXECUTION_GAP owner={Owner} run={Run} from={BeforePhase} to={Phase} elapsedMs={Elapsed:F1} gcPauseDeltaMs={GcPause:F1} pendingWorkItems={Pending} threadPoolThreads={Threads} sourceBefore={BeforeSource} sourceAfter={Source}; resumed observation, not proof of a specific cause",
                owner, run, beforePhase, phase, elapsed,
                Math.Max(0, (_gcPause() - beforePause).TotalMilliseconds), ThreadPool.PendingWorkItemCount,
                ThreadPool.ThreadCount, beforeSource, sourceSequence);
        }
        catch { /* 取证失败不改变业务调用。 */ }
    }

    internal void Mark(string phase, long sourceSequence = 0)
    {
        try
        {
            var now = _clock.GetTimestamp();
            var pause = _gcPause();
            var before = _previous;
            var beforePause = _previousGcPause;
            var beforePhase = _previousPhase;
            var beforeSource = _previousSource;
            _previous = now;
            _previousGcPause = pause;
            _previousPhase = phase;
            _previousSource = sourceSequence;
            if (before is not { } timestamp || _clock.GetElapsedTime(timestamp, now).TotalSeconds < 2) return;
            Report(timestamp, beforePause, beforePhase, phase, beforeSource, sourceSequence);
        }
        catch { /* 诊断失败不能改变业务结果或重新启动应用宿主。 */ }
    }
}
