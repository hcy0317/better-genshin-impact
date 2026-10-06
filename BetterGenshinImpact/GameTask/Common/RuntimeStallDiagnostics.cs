using System;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common;

// 单循环所有者；不采图、不访问App或进程服务。Measure/Mark报告恢复后耗时，Watch有界报告仍在执行的阶段。
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

    // 只报告仍在进行的阶段；不截图、不发输入、不取消原调用。最多4次，正常快调用静默。
    internal IDisposable Watch(string phase)
    {
        try { return new ActivePhase(this, phase); }
        catch { return NoObservation.Instance; }
    }
    private sealed class NoObservation : IDisposable
    {
        internal static readonly NoObservation Instance = new();
        public void Dispose() { }
    }

    private sealed class ActivePhase : IDisposable
    {
        private readonly RuntimeStallDiagnostics _owner;
        private readonly string _phase;
        private readonly long _started;
        private readonly TimeSpan _pause;
        private readonly int _thread = Environment.CurrentManagedThreadId;
        private ITimer? _timer;
        private int _closed, _reports;
        internal ActivePhase(RuntimeStallDiagnostics owner, string phase)
        {
            _owner = owner; _phase = phase; _started = owner._clock.GetTimestamp();
            try
            {
                _pause = owner._gcPause();
                // Timer不继承业务AsyncLocal，避免延长脚本/输入租约生命周期。
                if (ExecutionContext.IsFlowSuppressed()) CreateTimer();
                else { using (ExecutionContext.SuppressFlow()) CreateTimer(); }
            }
            catch { }
        }
        private void CreateTimer() => _timer = _owner._clock.CreateTimer(_ => Tick(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
        private void Tick()
        {
            if (Volatile.Read(ref _closed) != 0 || Interlocked.Increment(ref _reports) > 4) return;
            try
            {
                _owner.Report(_started, _pause, "active-thread-" + _thread, _phase + "-still-running", 0, 0, active: true);
            }
            catch { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _timer?.Dispose();
            _owner.Report(_started, _pause, "before-" + _phase, "after-" + _phase, 0, 0);
        }
    }

    internal readonly struct PhaseMeasurement(RuntimeStallDiagnostics? owner, string phase, long started, TimeSpan pause) : IDisposable
    {
        public void Dispose() => owner?.Report(started, pause, "before-" + phase, "after-" + phase, 0, 0);
    }

    private void Report(long started, TimeSpan beforePause, string? beforePhase, string phase, long beforeSource, long sourceSequence, bool active = false)
    {
        try
        {
            var elapsed = _clock.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed < 2000) return;
            logger.LogWarning("RUNTIME_EXECUTION_GAP owner={Owner} run={Run} from={BeforePhase} to={Phase} elapsedMs={Elapsed:F1} gcPauseDeltaMs={GcPause:F1} pendingWorkItems={Pending} threadPoolThreads={Threads} sourceBefore={BeforeSource} sourceAfter={Source} observation={Observation}; not proof of a specific cause",
                owner, run, beforePhase, phase, elapsed,
                Math.Max(0, (_gcPause() - beforePause).TotalMilliseconds), ThreadPool.PendingWorkItemCount,
                ThreadPool.ThreadCount, beforeSource, sourceSequence, active ? "still-running-threadpool-watch" : "resumed");
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
