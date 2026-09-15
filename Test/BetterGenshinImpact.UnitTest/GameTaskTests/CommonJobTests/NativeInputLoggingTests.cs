using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class NativeInputLoggingTests
{
    [Fact]
    public void DelayedFirstNativeCheckRecordsNoPauseFocusOrInputWork()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        using (UiOperation.Begin("combat-host-input", TimeSpan.FromMilliseconds(150), logger: logger, clock: clock))
        {
            clock.Advance(TimeSpan.FromMilliseconds(292));
            // 与S26相同的生产入口：第一次Check已超时，不能触达焦点/暂停或原生按键。
            Assert.Throws<TimeoutException>(() => TaskControl.CheckAndSleep(0));
        }
        var ended = logger.Events.Single(e => e["{OriginalFormat}"]!.ToString()!.StartsWith("UI_END"));
        Assert.Equal(292d, ended["FirstCheckAtMs"]);
        Assert.Equal(0d, ended["ChecksMs"]);
        Assert.Null(ended["PauseMs"]);
        Assert.Null(ended["FocusMs"]);
        Assert.Null(ended["NativeInputMs"]);
        Assert.Null(ended["ExplicitWaitMs"]);
    }

    [Fact]
    public async Task FullQueueDoesNotBlockAndShutdownKeepsTheActiveSinkAliveUntilDrained()
    {
        var sink = new BlockingSink();
        await using var pipeline = new ApplicationLogPipeline(
            new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink), capacity: 2);
        pipeline.Logger.Information("hold-output");
        try
        {
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 10; i++) pipeline.Logger.Information("queued {Index}", i);
            Assert.Equal(2, pipeline.Pending);
            Assert.Equal(8, pipeline.Dropped);
            Assert.Null(pipeline.LastOutputMilliseconds);
            var firstStop = pipeline.DisposeAsync().AsTask();
            var secondStop = pipeline.DisposeAsync().AsTask();
            Assert.Same(firstStop, secondStop);
            Assert.False(firstStop.IsCompleted);
            Assert.Equal(0, sink.DisposeCalls);
            sink.Release.TrySetResult();
            await firstStop.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, pipeline.Pending);
            Assert.Equal(1, sink.DisposeCalls);
            Assert.NotNull(pipeline.LastQueueMilliseconds);
            Assert.NotNull(pipeline.LastOutputMilliseconds);
        }
        finally { sink.Release.TrySetResult(); }
    }

    [Fact]
    public async Task BlockedOutputDoesNotConsumeTheHostInputFirstCheckBudget()
    {
        var sink = new BlockingSink();
        await using var pipeline = new ApplicationLogPipeline(
            new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink));
        using var provider = new SerilogLoggerProvider(pipeline.Logger, dispose: false);
        var inputOwner = Task.Run(() =>
        {
            using var operation = UiOperation.Begin("combat-host-input", TimeSpan.FromMilliseconds(150),
                logger: provider.CreateLogger("input-probe"));
            operation.Check();
        });
        try
        {
            await sink.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 接收器保持阻塞；输入owner必须独立完成检查，而不是依靠放宽150ms。
            Assert.Same(inputOwner, await Task.WhenAny(inputOwner, Task.Delay(300)));
            await inputOwner;
        }
        finally
        {
            sink.Release.TrySetResult();
            try { await inputOwner; } catch { /* 原断言/首检异常优先。 */ }
        }
    }

    private sealed class BlockingSink : ILogEventSink, IDisposable
    {
        public int DisposeCalls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(LogEvent logEvent)
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
        }
        public void Dispose() => Interlocked.Increment(ref DisposeCalls);
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<Dictionary<string, object?>> Events { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Events.Add(((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary());
    }
}
