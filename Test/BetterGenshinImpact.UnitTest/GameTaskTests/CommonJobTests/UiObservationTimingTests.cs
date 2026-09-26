using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class UiObservationTimingTests
{
    [Fact]
    public async Task ObservedCostsRemainDistinctFromPhasesThatDidNotRun()
    {
        var clock = new FakeTimeProvider();
        var logger = new EndLogger();
        await UiOperation.RunAsync("measured", TimeSpan.FromSeconds(1), default, operation =>
        {
            using (operation.Measure(UiOperationPhase.Capture)) clock.Advance(TimeSpan.FromMilliseconds(17));
            using (operation.Measure(UiOperationPhase.SceneRecognition)) clock.Advance(TimeSpan.FromMilliseconds(29));
            return Task.FromResult(true);
        }, logger, clock);
        Assert.Equal(17d, logger.End["CaptureMs"]);
        Assert.Equal(29d, logger.End["SceneMs"]);
        Assert.Null(logger.End["AreaOcrMs"]);
    }

    private sealed class EndLogger : ILogger
    {
        internal Dictionary<string, object?> End { get; private set; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter(state, exception).StartsWith("UI_END"))
                End = ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value);
        }
    }
}
