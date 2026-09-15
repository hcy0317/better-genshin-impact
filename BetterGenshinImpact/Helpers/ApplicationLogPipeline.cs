using System;
using System.Diagnostics;
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
        private readonly Channel<(LogEvent Event, long QueuedAt)> _queue;
        private double _lastQueueMilliseconds = double.NaN;
        private double _lastOutputMilliseconds = double.NaN;
        private long _reportedDrops;
        public long Dropped;
        public int Pending => _queue.Reader.Count;
        public Task Completion { get; }
        public double? LastQueueMilliseconds => Known(Volatile.Read(ref _lastQueueMilliseconds));
        public double? LastOutputMilliseconds => Known(Volatile.Read(ref _lastOutputMilliseconds));

        public QueuedSink(Logger output, int capacity)
        {
            _output = output;
            _queue = Channel.CreateBounded<(LogEvent, long)>(new BoundedChannelOptions(capacity)
            {
                SingleReader = true,
                FullMode = BoundedChannelFullMode.Wait,
                AllowSynchronousContinuations = false
            });
            Completion = Task.Run(DrainAsync);
        }

        public void Emit(LogEvent logEvent)
        {
            // 仅TryWrite，不等待空位；队列满不能延误输入或无限增加内存。
            if (!_queue.Writer.TryWrite((logEvent, Stopwatch.GetTimestamp())))
                Interlocked.Increment(ref Dropped);
        }

        private async Task DrainAsync()
        {
            try
            {
                await foreach (var entry in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
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
                            _output.Warning("LOG_QUEUE_DROPPED count={Count} total={Total}", dropped - _reportedDrops, dropped);
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
        public void Dispose() => _queue.Writer.TryComplete();
    }
}
