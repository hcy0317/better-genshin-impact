using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Service;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class ScriptRunLogTests
{
    [Theory]
    [InlineData(ScriptOutcomeKind.Completed)]
    [InlineData(ScriptOutcomeKind.Failed)]
    [InlineData(ScriptOutcomeKind.Skipped)]
    [InlineData(ScriptOutcomeKind.Cancelled)]
    public void EndMarkerPreservesParserCompatibilityButStatesActualOutcome(ScriptOutcomeKind kind)
    {
        var logger = new ReplayLogger();
        ScriptService.LogRunEnd(logger, "route", kind, TimeSpan.FromMilliseconds(61234));
        Assert.Contains(logger.Lines, line => line.StartsWith("→ 脚本执行结束:") &&
            line.Contains($"结果: {kind}") && line.Contains("1分1.234秒"));
    }

    [Fact]
    public void BrokenLogSinkCannotReplaceTheExecutionResult()
    {
        Assert.Null(Record.Exception(() => ScriptService.LogRunEnd(new ReplayLogger { Throw = true },
            "route", ScriptOutcomeKind.Failed, TimeSpan.Zero)));
    }

    private sealed class ReplayLogger : ILogger
    {
        internal readonly List<string> Lines = [];
        internal bool Throw;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (Throw) throw new IOException("log sink failed");
            Lines.Add(formatter(state, exception));
        }
    }
}
