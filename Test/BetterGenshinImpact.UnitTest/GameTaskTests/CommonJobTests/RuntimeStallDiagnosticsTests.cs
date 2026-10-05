using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class RuntimeStallDiagnosticsTests
{
    [Fact]
    public void IsolatedSlowPhaseExcludesIdleTimeAndRetainsGcEvidence()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLog();
        var pause = TimeSpan.Zero;
        var probe = new RuntimeStallDiagnostics(logger, "ui", "operation", clock, () => pause);
        clock.Advance(TimeSpan.FromMinutes(5));
        using (probe.Measure("Capture")) clock.Advance(TimeSpan.FromMilliseconds(10));
        Assert.Empty(logger.Messages);
        using (probe.Measure("SceneRecognition"))
        {
            clock.Advance(TimeSpan.FromSeconds(502));
            pause += TimeSpan.FromMilliseconds(7);
        }
        var message = Assert.Single(logger.Messages);
        Assert.Contains("from=before-SceneRecognition to=after-SceneRecognition", message);
        Assert.Contains("elapsedMs=502000", message);
        Assert.Contains("gcPauseDeltaMs=7", message);
    }

    [Fact]
    public void ReportsTheActualGapAndGcDeltaWithoutClaimingItsCause()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLog();
        var pause = TimeSpan.FromMilliseconds(10);
        var probe = new RuntimeStallDiagnostics(logger, "perception", "battle", clock, () => pause);
        probe.Mark("before-capture", 7);
        clock.Advance(TimeSpan.FromSeconds(214));
        pause += TimeSpan.FromMilliseconds(8);
        probe.Mark("capture-complete", 8);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("elapsedMs=214000", message);
        Assert.Contains("gcPauseDeltaMs=8", message);
        Assert.Contains("from=before-capture to=capture-complete", message);
        Assert.Contains("sourceBefore=7 sourceAfter=8", message);
        Assert.Contains("not proof", message);
        probe.Mark("next", 8);
        Assert.Single(logger.Messages);
    }

    [Fact]
    public void OrdinaryFramesStaySilentAndLoggerFailureCannotEscape()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLog { Throw = true };
        var probe = new RuntimeStallDiagnostics(logger, "host", "battle", clock, () => TimeSpan.Zero);
        probe.Mark("begin");
        clock.Advance(TimeSpan.FromMilliseconds(50));
        probe.Mark("end");
        Assert.Empty(logger.Messages);
        clock.Advance(TimeSpan.FromSeconds(3));
        probe.Mark("resumed");
    }

    private sealed class RecordingLog : ILogger
    {
        internal List<string> Messages { get; } = [];
        internal bool Throw { get; init; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Throw) throw new IOException("offline diagnostic sink failure");
            Messages.Add(formatter(state, exception));
        }
    }
}
