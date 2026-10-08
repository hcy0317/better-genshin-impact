using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.Common;

// 单循环所有者；不采图、不访问App或进程服务。Measure/Mark报告恢复后耗时，Watch有界报告仍在执行的阶段。
internal sealed class RuntimeStallDiagnostics(ILogger logger, string owner, string run,
    TimeProvider? clock = null, Func<TimeSpan>? gcPause = null, Func<RuntimeStallDiagnostics.ProcessSnapshot>? processSample = null)
{
    internal readonly record struct ProcessSnapshot(long Timestamp, double CpuMilliseconds, long PrivateBytes, long WorkingSet);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Func<TimeSpan> _gcPause = gcPause ?? GC.GetTotalPauseDuration;
    private long? _previous;
    private TimeSpan _previousGcPause;
    private string? _previousPhase;
    private long _previousSource;
    private readonly object _processGate = new();
    private ProcessSnapshot? _previousProcess;

    private string ReadProcessWindow()
    {
        try
        {
            ProcessSnapshot sample;
            if (processSample != null) sample = processSample();
            else
            {
                using var process = Process.GetCurrentProcess();
                sample = new(_clock.GetTimestamp(), process.TotalProcessorTime.TotalMilliseconds,
                    process.PrivateMemorySize64, process.WorkingSet64);
            }
            lock (_processGate)
            {
                var before = _previousProcess;
                if (before == null || sample.Timestamp > before.Value.Timestamp) _previousProcess = sample;
                var values = FormattableString.Invariant($"selfProcessSampleTimestamp={sample.Timestamp} selfCpuMs={sample.CpuMilliseconds:F1} selfPrivateBytes={sample.PrivateBytes} selfWorkingSetBytes={sample.WorkingSet}");
                if (before is not { } previous || sample.Timestamp <= previous.Timestamp)
                    return values + " selfProcessWindow=unknown:no-earlier-sample";
                return values + FormattableString.Invariant($" selfProcessWindowMs={_clock.GetElapsedTime(previous.Timestamp, sample.Timestamp).TotalMilliseconds:F1} selfCpuDeltaMs={sample.CpuMilliseconds - previous.CpuMilliseconds:F1} selfPrivateBytesDelta={sample.PrivateBytes - previous.PrivateBytes} selfWorkingSetDelta={sample.WorkingSet - previous.WorkingSet}");
            }
        }
        catch { return "selfProcessWindow=unavailable:sample-failed"; }
    }

    internal PhaseMeasurement Measure(string phase, long sourceSequence = 0)
    {
        try { return new(this, phase, _clock.GetTimestamp(), _gcPause(), sourceSequence); }
        catch { return default; }
    }

    // 只报告仍在进行的阶段；不截图、不发输入、不取消原调用。最多4次，正常快调用静默。
    internal IDisposable Watch(string phase, long sourceSequence = 0)
    {
        try { return new ActivePhase(this, phase, sourceSequence); }
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
        private readonly long _sourceSequence;
        private ITimer? _timer;
        private int _closed, _reports;
        internal ActivePhase(RuntimeStallDiagnostics owner, string phase, long sourceSequence)
        {
            _sourceSequence = sourceSequence;
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
                _owner.Report(_started, _pause, "active-thread-" + _thread, _phase + "-still-running", _sourceSequence, _sourceSequence, active: true);
            }
            catch { }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            _timer?.Dispose();
            _owner.Report(_started, _pause, "before-" + _phase, "after-" + _phase, _sourceSequence, _sourceSequence);
        }
    }

    internal readonly struct PhaseMeasurement(RuntimeStallDiagnostics? owner, string phase, long started, TimeSpan pause, long sourceSequence) : IDisposable
    {
        public void Dispose() => owner?.Report(started, pause, "before-" + phase, "after-" + phase, sourceSequence, sourceSequence);
    }

    private void Report(long started, TimeSpan beforePause, string? beforePhase, string phase, long beforeSource, long sourceSequence, bool active = false)
    {
        try
        {
            var elapsed = _clock.GetElapsedTime(started).TotalMilliseconds;
            if (elapsed < 2000) return;
            // 仅在既有慢阶段报送时采样自身，普通快阶段无进程读取开销。
            var process = ReadProcessWindow();
            logger.LogWarning("RUNTIME_EXECUTION_GAP owner={Owner} run={Run} from={BeforePhase} to={Phase} elapsedMs={Elapsed:F1} gcPauseDeltaMs={GcPause:F1} pendingWorkItems={Pending} threadPoolThreads={Threads} sourceBefore={BeforeSource} sourceAfter={Source} observation={Observation} {Process}; not proof of a specific cause",
                owner, run, beforePhase, phase, elapsed,
                Math.Max(0, (_gcPause() - beforePause).TotalMilliseconds), ThreadPool.PendingWorkItemCount,
                ThreadPool.ThreadCount, beforeSource, sourceSequence, active ? "still-running-threadpool-watch" : "resumed", process);
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
