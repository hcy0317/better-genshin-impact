using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowDiagnosticsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BrokenDiagnosticLoggerCannotReplaceCancellationOrPreventGameDisposal(bool brokenLevelCheck)
    {
        var clock = new FakeTimeProvider();
        var expected = new OperationCanceledException("原始游戏取消");
        var game = new SamplingGame(clock) { Failure = expected };
        var logger = new RecordingLogger { ThrowOnWrite = true, ThrowOnLevelCheck = brokenLevelCheck };
        using var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Actions = [new() { Character = "琴", Action = "attack(0.1,required)" }]
        }, game, clock: clock, logger: logger)!;
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            for (var i = 0; i < 40; i++) await runner.StepAsync(default);
        });
        Assert.Same(expected, actual);
        runner.Dispose();
        Assert.True(game.Disposed);
        Assert.Equal(1, game.InputCalls);
        Assert.Equal("Cancelled", Assert.Single(runner.RuntimeStatistics.RecentEvents).ReportedResult);
    }

    [Fact]
    public async Task NormalCompletionFlushesTheRemainingActionOnDispose()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        using var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Actions = [new() { Character = "琴", Action = "attack(0.1,required)" }]
        }, new SamplingGame(clock), clock: clock, logger: logger)!;
        for (var i = 0; i < 40 && runner.RuntimeStatistics.GameActionCalls == 0; i++) await runner.StepAsync(default);
        Assert.DoesNotContain(logger.Messages, message => message.StartsWith("FIGHT_ACTION "));
        runner.Dispose();
        Assert.Single(logger.Messages.Where(message => message.StartsWith("FIGHT_ACTION ") && message.Contains("boundary=dispose")));
    }

    [Fact]
    public void DecisionReportsAreThrottledAndDisabledLoggersNeverEvaluateSnapshots()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger { Enabled = false };
        var diagnostics = new CombatDecisionDiagnostics(logger, clock);
        var snapshots = 0;
        string Snapshot() { snapshots++; return "suppressed=5 published=0"; }
        Assert.False(diagnostics.Write("FIGHT_PERCEPTION", "battle", Snapshot));
        Assert.Equal(0, snapshots);
        logger.Enabled = true;
        Assert.True(diagnostics.Write("FIGHT_PERCEPTION", "battle", Snapshot));
        for (var i = 0; i < 100; i++) Assert.False(diagnostics.Write("FIGHT_PERCEPTION", "battle", Snapshot));
        Assert.Equal(1, snapshots);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(diagnostics.Write("FIGHT_PERCEPTION", "battle", Snapshot));
        Assert.Equal(2, logger.Messages.Count);
        Assert.True(diagnostics.Write("FIGHT_PERCEPTION", "battle", Snapshot, force: true));
        Assert.Equal(3, snapshots);
        logger.ThrowOnWrite = true;
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.False(diagnostics.Write("FIGHT_PERCEPTION", "battle", Snapshot));
    }

    [Fact]
    public async Task NativeHostReportsTheFirstFailedPassBeforeDisposalAndDoesNotRepeatItsTrace()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        using var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Actions = [new() { Character = "琴", Action = "e(required)" }]
        }, new FailedGame(clock), clock: clock, logger: logger)!;
        var completed = false;
        for (var i = 0; i < 40 && !completed; i++) completed = (await runner.StepAsync(default)).RoundCompleted;
        Assert.True(completed);
        Assert.Contains(logger.Messages, message => message.StartsWith("FIGHT_ACTION ") && message.Contains("boundary=failed-pass"));
        var count = logger.Messages.Count(message => message.StartsWith("FIGHT_ACTION "));
        runner.Dispose();
        Assert.Equal(count, logger.Messages.Count(message => message.StartsWith("FIGHT_ACTION ")));
    }

    [Fact]
    public async Task ActionEvidenceKeepsBoundedSanitizedSamplesAndRequestIdentityWithoutChangingExecution()
    {
        var clock = new FakeTimeProvider();
        var game = new SamplingGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("琴 attack(0.1)"), game, clock);
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        var entry = Assert.Single(execution.RuntimeStatistics.RecentEvents);
        Assert.False(string.IsNullOrWhiteSpace(entry.CommandId));
        Assert.Equal(game.AttemptId, entry.AttemptId);
        Assert.Equal(0d, entry.InputAt);
        Assert.Equal(32, entry.Observations.Count);
        Assert.Contains("sample=63", entry.Observations[^1]);
        Assert.All(entry.Observations, value =>
        {
            Assert.DoesNotContain('\n', value);
            Assert.InRange(value.Length, 1, 300);
        });
        Assert.Equal(1, game.InputCalls);

        game.Enabled = false;
        await execution.RunRoundAsync();
        Assert.Empty(execution.RuntimeStatistics.RecentEvents[^1].Observations);
        Assert.Equal(2, game.InputCalls);
    }

    [Fact]
    public async Task FailureAndExitReportsEmitEachAvailableActionOnceAndReportOverwrittenHistory()
    {
        var clock = new FakeTimeProvider();
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("琴 attack(0.1)"),
            new DiagnosticGame(clock), clock);
        var logger = new RecordingLogger();
        var writer = new CombatFlowDiagnosticWriter(logger);
        await execution.RunRoundAsync();
        writer.Write(execution.Context.BattleId, execution.RuntimeStatistics, "failed-pass");
        writer.Write(execution.Context.BattleId, execution.RuntimeStatistics, "repeated-failure");
        Assert.Single(logger.Messages.Where(message => message.StartsWith("FIGHT_ACTION ")));

        for (var i = 0; i < 70; i++) await execution.RunRoundAsync();
        writer.Write(execution.Context.BattleId, execution.RuntimeStatistics, "dispose");
        writer.Write(execution.Context.BattleId, execution.RuntimeStatistics, "dispose");
        Assert.Equal(65, logger.Messages.Count(message => message.StartsWith("FIGHT_ACTION ")));
        Assert.Single(logger.Messages.Where(message => message.StartsWith("FIGHT_TRACE_GAP ")));
        Assert.Contains(logger.Messages, message => message.Contains("seq=71 "));
    }

    [Fact]
    public async Task WrappedHistoryRetainsBattleLocalMonotonicSequenceForIncrementalFailureReports()
    {
        var program = CombatFlowProgram.Compile("琴 attack(0.1)");
        var clock = new FakeTimeProvider();
        using var execution = new CombatFlowExecution(program, new DiagnosticGame(clock), clock);
        for (var pass = 0; pass < 70; pass++) await execution.RunRoundAsync();

        Assert.Equal(Enumerable.Range(7, 64).Select(value => (long)value),
            execution.RuntimeStatistics.RecentEvents.Select(entry => entry.Sequence));
        using var next = new CombatFlowExecution(program, new DiagnosticGame(clock), clock);
        await next.RunRoundAsync();
        Assert.Equal(1, Assert.Single(next.RuntimeStatistics.RecentEvents).Sequence);
    }

    [Fact]
    public async Task RuntimeDiagnosticsKeepBoundedRecentEventsAndNeverBecomeNextBattleState()
    {
        var program = CombatFlowProgram.Compile("琴 attack(0.1,if=e-ready(琴),record=动作)");
        var clock = new FakeTimeProvider();
        using var execution = new CombatFlowExecution(program, new DiagnosticGame(clock), clock);
        for (var pass = 0; pass < 100; pass++) await execution.RunRoundAsync();
        var statistics = execution.RuntimeStatistics;
        Assert.Equal(100, statistics.GameActionCalls);
        Assert.Equal(100, statistics.CompletedPasses);
        Assert.True(statistics.GameObservationCalls >= 100);
        Assert.InRange(statistics.RecentEvents.Count, 1, 64);
        Assert.All(statistics.RecentEvents, entry => Assert.Equal("测试边界证据", entry.Reason));
        Assert.True(statistics.CoreStepMilliseconds >= 0);
        execution.Dispose();
        Assert.Null(execution.Context.Find("动作"));
        using var next = new CombatFlowExecution(program, new DiagnosticGame(clock), clock);
        Assert.Equal(0, next.RuntimeStatistics.GameActionCalls);
        Assert.Empty(next.RuntimeStatistics.RecentEvents);
        Assert.Null(next.Context.Find("动作"));
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public bool Enabled { get; set; } = true;
        public bool ThrowOnWrite { get; set; }
        public bool ThrowOnLevelCheck { get; set; }
        public bool IsEnabled(LogLevel logLevel) => ThrowOnLevelCheck
            ? throw new InvalidOperationException("日志级别查询故障") : Enabled;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (ThrowOnWrite) throw new InvalidOperationException("日志接收器故障");
            Messages.Add(formatter(state, exception));
        }
    }

    private sealed class SamplingGame(FakeTimeProvider clock) : ICombatFlowGame, IDisposable
    {
        public Guid AttemptId { get; } = Guid.NewGuid();
        public bool Enabled { get; set; } = true;
        public int InputCalls { get; private set; }
        public Exception? Failure { get; set; }
        public bool Disposed { get; private set; }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => true;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            action.CaptureDiagnostics = Enabled;
            action.DiagnosticAttemptId = AttemptId;
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            InputCalls++;
            for (var i = 0; i < 64; i++) action.Trace("e-ocr", $"sample={i}\nraw=" + new string('x', 500));
            clock.Advance(TimeSpan.FromMilliseconds(100));
            if (Failure != null) throw Failure;
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public void Dispose() => Disposed = true;
    }

    private sealed class FailedGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => true;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            action.TryBeginInput();
            clock.Advance(TimeSpan.FromSeconds(1));
            return ValueTask.FromResult(CombatFlowResult.Failed);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public ValueTask WaitAfterFailedPassAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class DiagnosticGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => true;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            action.DiagnosticReason = "测试边界证据";
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
