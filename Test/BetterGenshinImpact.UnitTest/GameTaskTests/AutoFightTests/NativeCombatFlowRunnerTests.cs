using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class NativeCombatFlowRunnerTests
{
    [Fact]
    public async Task PendingRequiredSkillDoesNotBecomeThreeHardFailuresOrUnlockTheOpening()
    {
        var game = new FailedSkillGame { Result = CombatFlowResult.Pending };
        using var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Info = new() { Declarations = ["segment(start,name=开场,define)\n琴 e(required)\nsegment(end,record=开场完成)"] },
            Actions = [new() { Character = "琴", Action = "call(开场,once=battle,required),attack(0.1)" }]
        }, game, clock: new FakeTimeProvider())!;
        var passes = 0;
        for (var i = 0; i < 50 && passes < 5; i++)
        {
            var step = await runner.StepAsync(default);
            if (!step.RoundCompleted) continue;
            passes++;
            Assert.Equal(CombatFlowResult.Deferred, step.Result);
            Assert.True(runner.TakeFinishCheckRequest());
        }
        Assert.Equal(5, passes);
        Assert.Null(runner.Context.Find("开场完成"));
        Assert.DoesNotContain(Method.Attack, game.Actions);
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("corrupt-file")]
    [InlineData("missing-character")]
    public async Task NativeEntryUsesInlineTimingWhenTheCatalogCannotSupplyTheCharacter(string catalogState)
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-native-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "skills.db");
        if (catalogState == "corrupt-file") File.WriteAllText(path, "This is not a SQLite database.");
        if (catalogState == "missing-character")
            new SkillCatalogStore(path).Import([new()
            {
                Id = "other.e", CharacterKey = "other", Character = "班尼特", Slot = "e",
                Revision = "offline-test", SourceUrl = "https://example.com/offline-test",
                Metrics = new() { ["duration"] = new() { Values = [20] } }
            }], "offline-test", DateTimeOffset.UtcNow);

        var strategy = new JsonCombatStrategy
        {
            Info = new() { Declarations = ["timing(随策略携带,cd=5,duration=8)"] },
            Actions = [new() { Character = "琴", Action = "e(timing=随策略携带,record=窗口),attack(0.1,keep=窗口)" }]
        };
        var game = new FailedSkillGame { Result = CombatFlowResult.Succeeded };
        using var runner = NativeCombatFlowRunner.Create(strategy, game,
            () => new SkillCatalogStore(path).ReadSnapshot(), new FakeTimeProvider());
        Assert.NotNull(runner);
        var completed = false;
        for (var step = 0; step < 10 && !completed; step++) completed = (await runner.StepAsync(default)).RoundCompleted;
        Assert.True(completed);
        Assert.Equal(8, runner.Context.Find("窗口")!.Duration);
        Assert.Equal(new[] { Method.Skill, Method.Attack }, game.Actions);
    }

    [Fact]
    public void InputReleaseFailureStillClosesTheGameAdapterAndBattle()
    {
        var game = new FailedSkillGame { ThrowOnRelease = true };
        var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        { Actions = [new() { Action = "record(标记)" }] }, game)!;
        Assert.Throws<InvalidOperationException>(() => runner.Dispose());
        Assert.False(runner.Context.IsOpen);
        Assert.True(game.Closed);
    }

    [Fact]
    public async Task CheckRequestsTheExistingHostDetectorWithoutSendingCharacterInput()
    {
        var strategy = new JsonCombatStrategy
        {
            Actions = [new() { Action = "record(检查前),check" }]
        };
        var game = new FailedSkillGame();
        using var runner = NativeCombatFlowRunner.Create(strategy, game, clock: new FakeTimeProvider());
        await runner!.StepAsync(default);
        Assert.Empty(game.Actions);
        Assert.True(runner.TakeFinishCheckRequest());
        Assert.False(runner.TakeFinishCheckRequest());
    }

    [Fact]
    public async Task FailedPassesAllowHostFinishDetectionBeforeStoppingWithoutReviveRetry()
    {
        var strategy = new JsonCombatStrategy
        {
            Info = new() { Declarations = ["segment(start,name=开场,define)\n琴 e(required)\nsegment(end,record=开场完成)"] },
            Actions = [new() { Character = "琴", Action = "call(开场,once=battle,required),attack(0.1)" }]
        };
        var game = new FailedSkillGame();
        using var runner = NativeCombatFlowRunner.Create(strategy, game, clock: new FakeTimeProvider());
        Assert.NotNull(runner);
        var battleId = runner.Context.BattleId;
        var failedPasses = 0;
        for (var i = 0; i < 30 && failedPasses < 3; i++)
        {
            var step = await runner.StepAsync(default);
            if (!step.RoundCompleted) continue;
            Assert.Equal(CombatFlowResult.Failed, step.Result);
            failedPasses++;
            Assert.True(runner.TakeFinishCheckRequest());
            Assert.False(runner.TakeFinishCheckRequest());
            Assert.Equal(battleId, runner.Context.BattleId);
            Assert.True(runner.Context.IsOpen);
        }
        Assert.Equal(3, failedPasses);
        var error = await Assert.ThrowsAsync<CombatNotFinishedException>(async () => await runner.StepAsync(default));
        Assert.IsNotAssignableFrom<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(error);
        Assert.Contains("未确认结束", error.Message);
        Assert.Null(runner.Context.Find("开场完成"));
        Assert.Equal(new[] { Method.Skill, Method.Skill, Method.Skill }, game.Actions);
        runner.Dispose();
        Assert.False(runner.Context.IsOpen);
        Assert.True(game.Closed);
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await runner.StepAsync(default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndependentHostDetectorCanEndBattleButPolicyFailureCannotReportVictory(bool detectorConfirmsEnd)
    {
        using var session = new CancellationTokenSource();
        using var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Actions = [new() { Character = "琴", Action = "e(required,record=已施放)" }]
        }, new FailedSkillGame(), clock: new FakeTimeProvider())!;
        var firstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detectorObserved = false;
        var host = NativeCombatTaskGroup.RunAsync(session, default,
            combat: async () =>
            {
                while (true)
                {
                    var step = await runner.StepAsync(session.Token);
                    if (step.RoundCompleted) firstFailure.TrySetResult();
                }
            },
            detectEnd: async () =>
            {
                await firstFailure.Task.WaitAsync(session.Token);
                detectorObserved = true;
                if (detectorConfirmsEnd) return;
                await Task.Delay(Timeout.Infinite, session.Token);
            });

        if (detectorConfirmsEnd) await host.WaitAsync(TimeSpan.FromSeconds(10));
        else await Assert.ThrowsAsync<CombatNotFinishedException>(() => host.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(detectorObserved);
        Assert.True(session.IsCancellationRequested);
        Assert.Null(runner.Context.Find("已施放"));
    }

    [Fact]
    public async Task SuccessfulPassClearsFailureStreakWithoutReplacingBattleRecords()
    {
        var game = new FailedSkillGame();
        using var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Actions = [new() { Character = "琴", Action = "e(required,record=成功施放)" }]
        }, game, clock: new FakeTimeProvider())!;
        async Task<CombatFlowResult> Pass()
        {
            for (var i = 0; i < 10; i++)
            {
                var step = await runner.StepAsync(default);
                if (step.RoundCompleted) return step.Result;
            }
            throw new Exception("遍历没有完成");
        }
        Assert.Equal(CombatFlowResult.Failed, await Pass());
        game.Result = CombatFlowResult.Succeeded;
        Assert.Equal(CombatFlowResult.Succeeded, await Pass());
        var record = runner.Context.Find("成功施放");
        Assert.NotNull(record);
        game.Result = CombatFlowResult.Failed;
        for (var i = 0; i < 3; i++) Assert.Equal(CombatFlowResult.Failed, await Pass());
        Assert.Same(record, runner.Context.Find("成功施放"));
        await Assert.ThrowsAsync<CombatNotFinishedException>(async () => await runner.StepAsync(default));
    }

    private sealed class FailedSkillGame : ICombatFlowGame, IDisposable
    {
        public List<Method> Actions { get; } = [];
        public bool Closed { get; private set; }
        public bool ThrowOnRelease { get; init; }
        public CombatFlowResult Result { get; set; } = CombatFlowResult.Failed;
        public void ReleaseHeldInput() { if (ThrowOnRelease) throw new InvalidOperationException("模拟释放失败"); }
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command.Method);
            return ValueTask.FromResult(Result);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
        public void Dispose() => Closed = true;
    }
}
