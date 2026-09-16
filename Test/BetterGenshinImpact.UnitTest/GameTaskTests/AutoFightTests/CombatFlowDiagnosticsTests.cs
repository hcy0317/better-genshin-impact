using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ASubmittedPendingActionDoesNotTurnEveryLaterPollIntoAnotherInputEvent()
    {
        var clock = new FakeTimeProvider();
        var diagnostics = new CombatFlowDiagnostics();
        var game = new DiagnosticCombatGame(new SubmissionThenWaitingGame(), diagnostics);
        using var context = new CombatFlowContext(clock);
        var action = new CombatFlowAction(new("琴", "e(required)"), context, () => true, 30);
        for (var index = 0; index < 301; index++) await game.ExecuteAsync(action, default);
        var events = diagnostics.Snapshot().RecentEvents;
        Assert.Contains(events, entry => entry.ReportedResult == "Pending");
        Assert.Equal(300, Assert.Single(events.Where(entry => entry.ReportedResult == "AwaitingObservation")).SampleCount);
    }

    private sealed class SubmissionThenWaitingGame : ICombatFlowGame
    {
        private bool _submitted;
        public bool ReportsInputReceipts => true;
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (_submitted) return ValueTask.FromResult(CombatFlowResult.AwaitingObservation);
            _submitted = true;
            action.TryBeginInput();
            action.RecordInputSubmission(action.InputRequestId);
            return ValueTask.FromResult(CombatFlowResult.Pending);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task WaitingForOneRequestRetainsThePrecedingInputEvidence()
    {
        var clock = new FakeTimeProvider();
        var game = new WaitingObservationGame(clock);
        using var execution = new CombatFlowExecution(
            CombatFlowProgram.Compile("琴 attack(0.1)\n琴 e(required)"), game, clock);
        for (var step = 0; step < 1000 && game.Observations < 300; step++)
            await execution.StepAsync(default);

        Assert.Equal(1, game.Inputs);
        Assert.Equal(300, game.Observations);
        var statistics = execution.RuntimeStatistics;
        Assert.Contains(statistics.RecentEvents, item => item.Action == "attack" && item.InputStarted);
        Assert.Contains(statistics.RecentEvents, item => item.ReportedResult == "AwaitingObservation");
        var waiting = Assert.Single(statistics.RecentEvents.Where(item => item.ReportedResult == "AwaitingObservation"));
        Assert.Equal(300, waiting.SampleCount);
        Assert.Equal(301, statistics.GameActionCalls);
        Assert.InRange(statistics.RecentEvents.Count, 2, 64);
        var logger = new RecordingLogger();
        new CombatFlowDiagnosticWriter(logger, clock).Write(execution.Context.BattleId, statistics, "failed-pass");
        Assert.Contains(logger.Messages, item => item.Contains("action=attack") && item.Contains("inputAdmitted=True"));
        Assert.DoesNotContain(logger.Messages, item => item.StartsWith("FIGHT_TRACE_GAP "));
        var periodic = new RecordingLogger();
        var writer = new CombatFlowDiagnosticWriter(periodic, clock);
        clock.Advance(TimeSpan.FromSeconds(30));
        writer.WritePeriodic(execution.Context.BattleId, () => statistics);
        Assert.Contains("sampled=2 dropped=0 coalesced=299", Assert.Single(periodic.Messages.Where(item => item.StartsWith("FIGHT_PROGRESS "))));
    }

    [Fact]
    public async Task PeriodicLoggingMeasuresInMemorySinkCostSeparatelyFromGameWork()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var writer = new CombatFlowDiagnosticWriter(logger, clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("琴 attack(0.1)"),
            new DiagnosticGame(clock), clock);
        var milliseconds = new List<double>();
        for (var period = 0; period < 64; period++)
        {
            for (var action = 0; action < 8; action++) await execution.RunRoundAsync();
            clock.Advance(TimeSpan.FromSeconds(30));
            var started = Stopwatch.GetTimestamp();
            writer.WritePeriodic(execution.Context.BattleId, () => execution.RuntimeStatistics);
            milliseconds.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        var ordered = milliseconds.Order().ToArray();
        output.WriteLine($"S21 periodic logging: samples=64 first={milliseconds[0]:F3}ms p50={ordered[31]:F3}ms p95={ordered[60]:F3}ms max={ordered[^1]:F3}ms; in-memory logger only, includes snapshot/formatting, excludes game work and production file I/O.");
        Assert.Equal(64, logger.Messages.Count(m => m.StartsWith("FIGHT_PROGRESS ")));
        Assert.Equal(512, logger.Messages.Count(m => m.StartsWith("FIGHT_ACTION ")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AContinuouslyRunningBattleReportsBoundedActionEvidenceBeforeDisposal(bool json)
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var game = new SamplingGame(clock);
        using var runner = CreateRunner(json, "attack(0.1,required)", game, clock, logger);

        for (var i = 0; i < 5000 && game.InputCalls < 350; i++) await runner.StepAsync(default);

        Assert.Equal(350, game.InputCalls);
        Assert.Single(logger.Messages.Where(message => message.StartsWith("FIGHT_PROGRESS ")));
        var actions = logger.Messages.Where(message => message.StartsWith("FIGHT_ACTION ") &&
            message.Contains("boundary=periodic")).ToArray();
        Assert.Equal(8, actions.Length);
        Assert.All(actions, message => Assert.DoesNotContain("samples=", message));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("boundary=dispose"));
    }

    [Fact]
    public void PeriodicDiagnosticsDoNotReadSnapshotsUntilDueOrWhenDisabledAndDoNotRetryFaultedSinks()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var writer = new CombatFlowDiagnosticWriter(logger, clock);
        var snapshots = 0;
        CombatFlowStatistics Read()
        {
            snapshots++;
            return new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, []);
        }
        var battle = Guid.NewGuid();
        clock.Advance(TimeSpan.FromSeconds(29));
        writer.WritePeriodic(battle, Read);
        Assert.Equal(0, snapshots);
        logger.Enabled = false;
        clock.Advance(TimeSpan.FromSeconds(1));
        writer.WritePeriodic(battle, Read);
        Assert.Equal(0, snapshots);
        logger.Enabled = true;
        writer.WritePeriodic(battle, Read);
        Assert.Equal(1, snapshots);
        Assert.Contains("sampled=0 dropped=0", Assert.Single(logger.Messages));
        logger.ThrowOnWrite = true;
        clock.Advance(TimeSpan.FromSeconds(30));
        writer.WritePeriodic(battle, Read);
        for (var i = 0; i < 100; i++) writer.WritePeriodic(battle, Read);
        Assert.Equal(2, snapshots);
        logger.ThrowOnLevelCheck = true;
        clock.Advance(TimeSpan.FromSeconds(30));
        writer.WritePeriodic(battle, Read);
        Assert.Equal(2, snapshots);
        logger.ThrowOnLevelCheck = false;
        writer.WritePeriodic(battle, () => { snapshots++; throw new InvalidOperationException("快照故障"); });
        writer.WritePeriodic(battle, Read);
        Assert.Equal(3, snapshots);
    }

    [Fact]
    public async Task PeriodicSamplingAccountsForDroppedHistoryAndNeverRepeatsItAtLaterBoundaries()
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var writer = new CombatFlowDiagnosticWriter(logger, clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("琴 attack(0.1)"),
            new DiagnosticGame(clock), clock);
        for (var i = 0; i < 70; i++) await execution.RunRoundAsync();
        clock.Advance(TimeSpan.FromSeconds(30));
        writer.WritePeriodic(execution.Context.BattleId, () => execution.RuntimeStatistics);
        Assert.Contains("sampled=8 dropped=62", Assert.Single(logger.Messages.Where(m => m.StartsWith("FIGHT_PROGRESS "))));
        Assert.Equal(8, logger.Messages.Count(m => m.StartsWith("FIGHT_ACTION ")));
        writer.Write(execution.Context.BattleId, execution.RuntimeStatistics, "failed-pass");
        Assert.Equal(8, logger.Messages.Count(m => m.StartsWith("FIGHT_ACTION ")));
        await execution.RunRoundAsync();
        writer.Write(execution.Context.BattleId, execution.RuntimeStatistics, "dispose");
        Assert.Contains("seq=71 ", logger.Messages.Last());
        Assert.Equal(9, logger.Messages.Count(m => m.StartsWith("FIGHT_ACTION ")));

        using var next = new CombatFlowExecution(CombatFlowProgram.Compile("琴 attack(0.1)"), new DiagnosticGame(clock), clock);
        var nextWriter = new CombatFlowDiagnosticWriter(logger, clock);
        await next.RunRoundAsync();
        nextWriter.WritePeriodic(next.Context.BattleId, () => throw new Exception("下一场尚未到周期"));
        clock.Advance(TimeSpan.FromSeconds(30));
        nextWriter.WritePeriodic(next.Context.BattleId, () => next.RuntimeStatistics);
        Assert.Contains("seq=1 ", logger.Messages.Last());
        Assert.Contains("sampled=1 dropped=0", logger.Messages.Last(m => m.StartsWith("FIGHT_PROGRESS ")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuePeriodicEvidenceWaitsForTheAtomicBoundaryWithoutChangingGameCalls(bool json)
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger();
        var game = new SamplingGame(clock);
        using var runner = CreateRunner(json,
            "segment(start,atomic,timeout=120),attack(0.1,required),attack(0.1,required),segment(end)", game, clock, logger);
        for (var i = 0; i < 100 && game.InputCalls == 0; i++) await runner.StepAsync(default);
        Assert.True(runner.IsAtomic);
        Assert.Equal(1, game.InputCalls);
        clock.Advance(TimeSpan.FromSeconds(30));
        var checkedInside = false;
        for (var i = 0; i < 100 && runner.IsAtomic; i++)
        {
            await runner.StepAsync(default);
            if (runner.IsAtomic)
            {
                checkedInside = true;
                Assert.DoesNotContain(logger.Messages, m => m.StartsWith("FIGHT_PROGRESS "));
            }
        }
        Assert.True(checkedInside);
        Assert.False(runner.IsAtomic);
        Assert.Equal(2, game.InputCalls);
        Assert.Single(logger.Messages.Where(m => m.StartsWith("FIGHT_PROGRESS ")));
    }

    private static NativeCombatFlowRunner CreateRunner(bool json, string action, ICombatFlowGame game,
        FakeTimeProvider clock, ILogger logger)
    {
        if (json)
            return NativeCombatFlowRunner.Create(new JsonCombatStrategy
            {
                Actions = [new() { Character = "琴", Action = action }]
            }, game, clock: clock, logger: logger)!;
        var program = CombatFlowProgram.Compile(new CombatScript(["琴"], CombatScriptParser.ParseLineCommands(action, "琴")));
        program.AllowHostLoop(true);
        return NativeCombatFlowRunner.Create(program, game, clock, logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APeriodicSinkFailureCannotReplaceLaterCancellationOrChangeGameInputCount(bool json)
    {
        var clock = new FakeTimeProvider();
        var logger = new RecordingLogger { ThrowOnWrite = true };
        var game = new SamplingGame(clock);
        using var runner = CreateRunner(json, "attack(0.1,required)", game, clock, logger);
        for (var i = 0; i < 5000 && game.InputCalls < 350; i++) await runner.StepAsync(default);
        Assert.Equal(350, game.InputCalls);
        var expected = new OperationCanceledException("周期日志失败之后取消");
        game.Failure = expected;
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            for (var i = 0; i < 40; i++) await runner.StepAsync(default);
        });
        Assert.Same(expected, actual);
        runner.Dispose();
        Assert.Equal(351, game.InputCalls);
        Assert.True(game.Disposed);
    }

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

    private sealed class WaitingObservationGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public int Inputs { get; private set; }
        public int Observations { get; private set; }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => true;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            clock.Advance(TimeSpan.FromMilliseconds(5));
            if (action.Command.Method == Method.Attack)
            {
                Assert.True(action.TryBeginInput());
                Inputs++;
                return ValueTask.FromResult(CombatFlowResult.Succeeded);
            }
            action.DiagnosticReason = "等待同一切人请求的新帧";
            Observations++;
            return ValueTask.FromResult(CombatFlowResult.AwaitingObservation);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
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
