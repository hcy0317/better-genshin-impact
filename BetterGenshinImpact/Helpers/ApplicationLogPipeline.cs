using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace BetterGenshinImpact.Helpers;

/// <summary>应用日志的构建及退役边界，可在不启动WPF宿主时验证接收器行为。</summary>
internal sealed class ApplicationLogPipeline : IAsyncDisposable
{
    private readonly QueuedSink _sink;
    public Logger Logger { get; }
    public long Dropped => Interlocked.Read(ref _sink.Dropped);
    public int Pending => _sink.Pending;
    public double? LastQueueMilliseconds => _sink.LastQueueMilliseconds;
    public double? LastOutputMilliseconds => _sink.LastOutputMilliseconds;

    public ApplicationLogPipeline(LoggerConfiguration outputs, int capacity = 1024)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _sink = new QueuedSink(outputs.CreateLogger(), capacity);
        Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Warning)
            .WriteTo.Sink(_sink)
            .CreateLogger();
    }

    public ValueTask DisposeAsync()
    {
        // 关闭准入不在UI线程同步等接收器；调用者须在WPF仍可调度时await排空。
        Logger.Dispose();
        return new ValueTask(_sink.Completion);
    }

    private sealed class QueuedSink : ILogEventSink, IDisposable
    {
        private readonly Logger _output;
        private readonly LinkedList<(LogEvent Event, long QueuedAt)> _queue = new();
        private readonly LinkedList<LinkedListNode<(LogEvent Event, long QueuedAt)>>[] _priorityQueues =
            [new(), new(), new(), new()];
        private readonly object _gate = new();
        private readonly int _capacity;
        private readonly Channel<byte> _wake = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite, AllowSynchronousContinuations = false });
        private bool _closed;
        private long _importantDropped;
        private double _lastQueueMilliseconds = double.NaN;
        private double _lastOutputMilliseconds = double.NaN;
        private long _reportedDrops;
        public long Dropped;
        public int Pending { get { lock (_gate) return _queue.Count; } }
        public Task Completion { get; }
        public double? LastQueueMilliseconds => Known(Volatile.Read(ref _lastQueueMilliseconds));
        public double? LastOutputMilliseconds => Known(Volatile.Read(ref _lastOutputMilliseconds));

        public QueuedSink(Logger output, int capacity)
        {
            _output = output;
            _capacity = capacity;
            Completion = Task.Run(DrainAsync);
        }

        public void Emit(LogEvent logEvent)
        {
            // 不等待writer；只有有界链表操作，重要日志替换低优先项但不扩大容量。
            lock (_gate)
            {
                if (_closed) { Drop(logEvent); return; }
                if (_queue.Count == _capacity)
                {
                    LinkedList<LinkedListNode<(LogEvent Event, long QueuedAt)>>? candidates = null;
                    for (var priority = 0; priority < Importance(logEvent); priority++)
                        if (_priorityQueues[priority].Count > 0) { candidates = _priorityQueues[priority]; break; }
                    if (candidates?.First == null)
                    { Drop(logEvent); return; }
                    var candidate = candidates.First.Value;
                    Drop(candidate.Value.Event);
                    _queue.Remove(candidate);
                    candidates.RemoveFirst();
                }
                var added = _queue.AddLast((logEvent, Stopwatch.GetTimestamp()));
                _priorityQueues[Importance(logEvent)].AddLast(added);
                _wake.Writer.TryWrite(0);
            }
        }

        private static int Importance(LogEvent item)
        {
            if (item.Level >= LogEventLevel.Error) return 3;
            if (item.Level >= LogEventLevel.Warning) return 2;
            var name = item.MessageTemplate.Text;
            return name.StartsWith("INPUT_", StringComparison.Ordinal) || name.StartsWith("NATIVE_INPUT", StringComparison.Ordinal) ||
                name.StartsWith("SELECTION_GOAL", StringComparison.Ordinal) || name.StartsWith("UI_END", StringComparison.Ordinal) ||
                name.StartsWith("EVIDENCE_", StringComparison.Ordinal) || name.StartsWith("HTTP_", StringComparison.Ordinal) ||
                name.StartsWith("PATH_HEALING_CHECKPOINT", StringComparison.Ordinal) || name.StartsWith("FIGHT_HOST_DECISION", StringComparison.Ordinal) ||
                name.StartsWith("FIGHT_STRATEGY", StringComparison.Ordinal) ? 1 : 0;
        }

        private void Drop(LogEvent item)
        {
            Interlocked.Increment(ref Dropped);
            if (Importance(item) > 0) Interlocked.Increment(ref _importantDropped);
        }

        private bool TryTake(out (LogEvent Event, long QueuedAt) entry)
        {
            lock (_gate)
            {
                if (_queue.First == null) { entry = default; return false; }
                entry = _queue.First.Value;
                _queue.RemoveFirst();
                _priorityQueues[Importance(entry.Event)].RemoveFirst();
                return true;
            }
        }

        private async Task DrainAsync()
        {
            try
            {
                await foreach (var _ in _wake.Reader.ReadAllAsync().ConfigureAwait(false))
                while (TryTake(out var entry))
                {
                    var queueMs = Stopwatch.GetElapsedTime(entry.QueuedAt).TotalMilliseconds;
                    Volatile.Write(ref _lastQueueMilliseconds, queueMs);
                    entry.Event.AddOrUpdateProperty(new LogEventProperty("LogQueueMs", new ScalarValue(queueMs)));
                    var started = Stopwatch.GetTimestamp();
                    try
                    {
                        var dropped = Interlocked.Read(ref Dropped);
                        if (dropped > _reportedDrops)
                        {
                            _output.Warning("LOG_QUEUE_DROPPED count={Count} total={Total} importantTotal={ImportantTotal}",
                                dropped - _reportedDrops, dropped, Interlocked.Read(ref _importantDropped));
                            _reportedDrops = dropped;
                        }
                        _output.Write(entry.Event);
                    }
                    catch { /* 接收器故障不传播给生产者，也不重放同一日志。 */ }
                    finally
                    {
                        Volatile.Write(ref _lastOutputMilliseconds, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    }
                }
            }
            finally
            {
                // 接收器正在Emit时绝不释放它；停止者可异步等待Completion。
                _output.Dispose();
            }
        }

        private static double? Known(double value) => double.IsNaN(value) ? null : value;
        public void Dispose()
        {
            lock (_gate) { _closed = true; _wake.Writer.TryComplete(); }
        }
    }
}
