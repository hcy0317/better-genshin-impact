using BetterGenshinImpact.GameTask.AutoPathing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathMovementDiagnosticsTests
{
    [Fact]
    public void NavigationDiagnosticsAreBoundedAndCannotTurnObservationOrLogFailuresIntoTaskFailures()
    {
        var clock = new FakeTimeProvider();
        var logger = new FailingLogger();
        var diagnostics = new PathMovementDiagnostics(logger, clock);
        var reads = 0;
        PathMovementObservation Read() { reads++; return default; }
        for (var frame = 0; frame < 200; frame++)
            Assert.Null(Record.Exception(() => diagnostics.Observe("segment1/point5", Read)));
        Assert.Equal(1, reads);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(Record.Exception(() => diagnostics.Observe("segment1/point5", () => throw new IOException("vision"))));
        Assert.Null(Record.Exception(() => diagnostics.Observe("segment1/point6", Read)));
        Assert.Equal(2, reads);
        logger.Enabled = false;
        diagnostics.Observe("segment1/point7", Read);
        Assert.Equal(2, reads);
    }

    private sealed class FailingLogger : ILogger
    {
        public bool Enabled { get; set; } = true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => Enabled;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new IOException("log");
    }
}
