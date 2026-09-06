using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowControlTests
{
    [Fact]
    public async Task ExhaustedRechargeCanRestartAfterAConfirmedBurstCreatesANewDemand()
    {
        var program = CombatFlowProgram.Compile("""
            香菱 q(recharge,from=班尼特,attempts=2,required)
            segment(供能,define) { 班尼特 e(feed=香菱) }
            """);
        var clock = new FakeTimeProvider();
        var game = new NewDemandGame();
        using var execution = new CombatFlowExecution(program, game, clock);
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Equal(2, game.Skills);
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Equal(2, game.Skills); // 时间和根回边本身不能解除耗尽。
        game.Ready = true;
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        game.SupplyWorks = true; // 前次 Q 已确认并消耗能量，下一次是独立需求。
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(3, game.Skills);
        Assert.Equal(2, game.Bursts);
    }

    [Fact]
    public async Task ARequiredActionFilteredOutOfTheOpeningDoesNotLatchOnce()
    {
        var program = CombatFlowProgram.Compile("""
            call(开场,once=battle,required)
            琴 attack(0.1,record=后继)
            segment(start,name=开场,define)
            钟离 round(even),e(required)
            segment(end,record=开场完成)
            """);
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Null(execution.Context.Find("开场完成"));
        Assert.Empty(game.Actions);
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "钟离:skill", "琴:attack" }, game.Actions);
    }

    [Fact]
    public async Task AnAtomicSegmentCanCreateItsOwnRequiredWindowBeforeConsumingIt()
    {
        var program = CombatFlowProgram.Compile("""
            timing(短窗口,duration=6)
            segment(start,atomic,timeout=8)
            琴 e(required,record=本次窗口,timing=短窗口)
            琴 attack(1,keep=本次窗口,required)
            segment(end,record=整段完成)
            """);
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        await execution.RunRoundAsync();
        Assert.Equal(new[] { "琴:skill", "琴:attack" }, game.Actions);
        Assert.NotNull(execution.Context.Find("整段完成"));
    }

    [Fact]
    public async Task AWatchFromAFailedOpeningCannotSkipTheOpeningOnTheNextRootTraversal()
    {
        var program = CombatFlowProgram.Compile("""
            timing(覆盖时间,duration=6)
            strategy(loop=battle)
            call(开场,once=battle,required)
            钟离 e(record=覆盖,maintain=覆盖,watch=覆盖,before=4,timing=覆盖时间)
            琴 attack(0.1,record=依赖输出)
            segment(start,name=开场,define)
            钟离 e(record=覆盖,watch=覆盖,before=4,timing=覆盖时间,required)
            琴 q(required)
            segment(end,record=开场完成)
            """);
        var clock = new FakeTimeProvider();
        using var execution = new CombatFlowExecution(program, new ControlGame(), clock);
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Null(execution.Context.Find("依赖输出"));
        Assert.Null(execution.Context.Find("开场完成"));
    }

    [Fact]
    public async Task OptionalFallbackDoesNotResolveAnExhaustedResumeEpisode()
    {
        var program = CombatFlowProgram.Compile("""
            call(检查)
            segment(start,name=检查,define)
            call(供能,resume=entry,attempts=2)
            琴 attack(0.1)
            segment(end)
            segment(start,name=供能,define)
            班尼特 e
            segment(end)
            """);
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        await execution.RunRoundAsync();
        await execution.RunRoundAsync();
        Assert.Equal(2, game.Actions.Count(action => action == "班尼特:skill"));
        Assert.Equal(2, game.Actions.Count(action => action == "琴:attack"));
    }

    [Fact]
    public async Task StandaloneRoundFilterBeforePipeAppliesToTheFollowingCall()
    {
        var program = CombatFlowProgram.Compile("""
            round(odd) | call(奇)
            round(even) | call(偶)
            segment(start,name=奇,define)
            班尼特 e
            segment(end)
            segment(start,name=偶,define)
            琴 e
            segment(end)
            """);
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        await execution.RunRoundAsync();
        await execution.RunRoundAsync();
        await execution.RunRoundAsync();
        Assert.Equal(new[] { "班尼特:skill", "琴:skill", "班尼特:skill" }, game.Actions);
    }

    [Fact]
    public async Task CallModeWatchMaintainsThenReturnsToTheInterruptedSuccessor()
    {
        var program = CombatFlowProgram.Compile("""
            timing(覆盖时间,duration=20)
            班尼特 e(record=覆盖,timing=覆盖时间,maintain=覆盖,watch=覆盖,watch-mode=call,watch-target=维护)
            香菱 attack(0.1,record=原后继)
            segment(start,name=维护,define)
            钟离 e(record=覆盖,timing=覆盖时间,maintain=覆盖)
            segment(end)
            """);
        var clock = new FakeTimeProvider();
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "班尼特:skill", "钟离:skill", "香菱:attack" }, game.Actions);
        Assert.NotNull(execution.Context.Find("原后继"));
        Assert.Equal(1, execution.Round);
    }

    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 5)]
    public async Task NoProgressLimitRequiresDistinctReliableResourceMeasurements(bool measured, int expectedAttempts)
    {
        var program = CombatFlowProgram.Compile("""
            香菱 q(recharge,from=班尼特,attempts=5,no-progress=2,required)
            segment(start,name=供能,define)
            班尼特 e(feed=香菱)
            segment(end)
            """);
        var game = new ControlGame { EnergyLow = true, Cooling = false, ReportEnergy = measured };
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        game.Context = execution.Context;
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Equal(expectedAttempts, game.Actions.Count(action => action == "班尼特:skill"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(true, null)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task UnknownEnergyAndCoolingBurstsNeverAuthorizeRecharge(bool? low, bool? cooling)
    {
        var program = CombatFlowProgram.Compile("""
            香菱 q(recharge,from=班尼特,required)
            segment(start,name=供能,define)
            班尼特 e(feed=香菱)
            segment(end)
            """);
        var game = new ControlGame { EnergyLow = low, Cooling = cooling };
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Empty(game.Actions);
    }

    [Fact]
    public async Task UnresolvedRechargeCannotGetNewAttemptsByChangingRoundOrCallSite()
    {
        var program = CombatFlowProgram.Compile("""
            香菱 q(recharge,from=班尼特,attempts=2)
            香菱 q(recharge,from=班尼特,attempts=4)
            琴 attack(0.1)
            segment(start,name=供能,define)
            班尼特 e(feed=香菱)
            segment(end)
            """);
        var game = new ControlGame { EnergyLow = true, Cooling = false };
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        await execution.RunRoundAsync();
        await execution.RunRoundAsync();
        Assert.Equal(2, game.Actions.Count(action => action == "班尼特:skill"));
        Assert.Equal(2, game.Actions.Count(action => action == "香菱:wait"));
        Assert.Equal(2, game.Actions.Count(action => action == "琴:attack"));
        Assert.DoesNotContain("香菱:burst", game.Actions);
    }

    [Fact]
    public async Task RechargeUsesTheDeclaredProducerThenFreshlyRechecksTheRequestedBurst()
    {
        var program = CombatFlowProgram.Compile("""
            香菱 q(recharge,from=班尼特,attempts=2,timeout=8,required)
            segment(start,name=供能,define)
            班尼特 e(fast,feed=香菱)
            segment(end)
            """);
        var game = new ControlGame { BecomesReadyAfterSkill = true, EnergyLow = true, Cooling = false };
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "班尼特:skill", "香菱:wait", "香菱:burst" }, game.Actions);
        Assert.Equal(1, execution.Round);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FeedFollowsOnlyAConfirmedProducerWithoutInterveningActions(bool failProducer)
    {
        var program = CombatFlowProgram.Compile("""
            班尼特 e(fast,feed=香菱,required)
            琴 attack(0.1)
            """);
        var game = new ControlGame { FailSkill = failProducer };
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(failProducer ? CombatFlowResult.Failed : CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(failProducer ? new[] { "班尼特:skill" } : new[] { "班尼特:skill", "香菱:wait", "琴:attack" }, game.Actions);
    }

    [Fact]
    public async Task WatchInTheCallerAbandonsNestedFramesAndContinuesAtItsOwnSuccessor()
    {
        var program = CombatFlowProgram.Compile("""
            strategy(loop=battle)
            timing(覆盖时间,duration=20)
            call(主轴)
            segment(start,name=主轴,define)
            班尼特 e(record=覆盖,timing=覆盖时间,maintain=覆盖,watch=覆盖)
            call(站场)
            钟离 e(record=覆盖,timing=覆盖时间,maintain=覆盖,watch=覆盖)
            琴 attack(0.1)
            segment(end)
            segment(start,name=站场,define)
            香菱 attack(0.1)
            琴 attack(0.1,record=旧余段)
            segment(end,record=站场完成)
            """);
        var clock = new FakeTimeProvider();
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        await execution.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "班尼特:skill", "香菱:attack", "钟离:skill", "琴:attack" }, game.Actions);
        Assert.Null(execution.Context.Find("旧余段"));
        Assert.Null(execution.Context.Find("站场完成"));
        Assert.Equal(1, execution.Round);
    }

    [Fact]
    public async Task WatchPassesTheConsumersLargerDemandToTheMaintenanceDestination()
    {
        var program = CombatFlowProgram.Compile("""
            timing(覆盖时间,duration=20)
            班尼特 e(record=覆盖,timing=覆盖时间,maintain=覆盖,watch=覆盖,before=4,required)
            香菱 attack(10,keep=覆盖,required)
            钟离 e(record=覆盖,timing=覆盖时间,maintain=覆盖,watch=覆盖,before=4,required)
            琴 attack(0.1)
            """);
        var clock = new FakeTimeProvider();
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "班尼特:skill", "钟离:skill", "琴:attack" }, game.Actions);
        Assert.Equal(12, execution.Context.Find("覆盖")!.OccurredAt);
    }

    [Fact]
    public async Task AtomicCoverageIsCheckedForTheWholeFragmentBeforeReusingMaintenance()
    {
        var program = CombatFlowProgram.Compile("""
            timing(覆盖时间,duration=20)
            班尼特 e(record=覆盖,timing=覆盖时间,maintain=覆盖,before=4,required)
            班尼特 e(record=覆盖,timing=覆盖时间,maintain=覆盖,before=4,required)
            segment(start,atomic)
            香菱 attack(3,keep=覆盖,required)
            香菱 attack(3,keep=覆盖,required)
            segment(end)
            """);
        var clock = new FakeTimeProvider();
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(13));
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "班尼特:skill", "班尼特:skill", "香菱:attack", "香菱:attack" }, game.Actions);
    }

    [Fact]
    public async Task MaintenanceUsesTheNextConsumersCoverageDemandNotOnlyBefore()
    {
        var program = CombatFlowProgram.Compile("""
            timing(覆盖时间,duration=20)
            班尼特 e(record=覆盖,timing=覆盖时间,maintain=覆盖,before=4,required)
            班尼特 e(record=覆盖,timing=覆盖时间,maintain=覆盖,before=4,required)
            香菱 attack(10,keep=覆盖,required)
            """);
        var clock = new FakeTimeProvider();
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(12));
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "班尼特:skill", "班尼特:skill", "香菱:attack" }, game.Actions);
        Assert.Equal(12, execution.Context.Find("覆盖")!.OccurredAt);
    }

    [Fact]
    public async Task RecoveryRunsOnceWithoutTurningTheFailedSegmentIntoSuccess()
    {
        var program = CombatFlowProgram.Compile("""
            call(开场,once=battle,required)
            香菱 attack(1)
            segment(start,name=开场,define,onfail=恢复)
            班尼特 e(required)
            香菱 attack(1)
            segment(end,record=开场完成)
            segment(start,name=恢复,define)
            琴 attack(0.1)
            segment(end)
            """);
        var game = new ControlGame { FailSkill = true };
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Equal(new[] { "班尼特:skill", "琴:attack" }, game.Actions);
        Assert.Null(execution.Context.Find("开场完成"));
    }

    [Fact]
    public async Task TailTransferDoesNotCompleteOrLatchTheAbandonedOpening()
    {
        var program = CombatFlowProgram.Compile("""
            call(开场,once=battle,required)
            香菱 attack(1)
            segment(start,name=开场,define)
            班尼特 e
            jump(安全)
            segment(end,record=开场完成)
            segment(start,name=安全,define)
            琴 attack(0.1)
            segment(end)
            """);
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(CombatFlowResult.Transferred, await execution.RunRoundAsync());
        Assert.Equal(CombatFlowResult.Transferred, await execution.RunRoundAsync());
        Assert.Equal(new[] { "班尼特:skill", "琴:attack", "班尼特:skill", "琴:attack" }, game.Actions);
        Assert.Null(execution.Context.Find("开场完成"));
    }

    [Fact]
    public async Task ReturningFromASwitchingChildRechecksTheCallersOnFieldRequirement()
    {
        var program = CombatFlowProgram.Compile("""
            香菱 attack(0.1)
            call(输出,required)
            segment(start,name=输出,define,requires=onfield(香菱))
            call(供能)
            香菱 attack(1)
            segment(end,record=输出完成)
            segment(start,name=供能,define)
            班尼特 e(required)
            segment(end)
            """);
        var game = new ControlGame();
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Equal(new[] { "香菱:attack", "班尼特:skill" }, game.Actions);
        Assert.Null(execution.Context.Find("输出完成"));
    }

    [Fact]
    public async Task ResumeEntryRechecksTheCallerWithoutAnotherInvocationOrRootRound()
    {
        var program = CombatFlowProgram.Compile("""
            call(输出,required)
            segment(start,name=输出,define)
            call(供能,if=!q-ready(香菱),resume=entry,attempts=2,timeout=8)
            香菱 q(required,if=odd(call-index(输出)))
            segment(end)
            segment(start,name=供能,define)
            班尼特 e(required)
            segment(end)
            """);
        var game = new ControlGame { BecomesReadyAfterSkill = true };
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "班尼特:skill", "香菱:burst" }, game.Actions);
        Assert.Equal(1, execution.Round);
    }

    private sealed class NewDemandGame : ICombatFlowGame
    {
        public bool Ready;
        public bool SupplyWorks;
        public int Skills, Bursts;
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => function switch
        { "q-ready" => Ready, "q-energy-low" => !Ready, "q-cd" => false, "e-ready" => true, _ => null };
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            if (action.Command.Method == Method.Skill) { Skills++; Ready |= SupplyWorks; }
            if (action.Command.Method == Method.Burst) { Bursts++; Ready = false; }
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class ControlGame : ICombatFlowGame
    {
        public bool BecomesReadyAfterSkill { get; init; }
        public bool FailSkill { get; init; }
        public bool? EnergyLow { get; init; }
        public bool? Cooling { get; init; }
        public bool ReportEnergy { get; init; }
        public CombatFlowContext? Context { get; set; }
        public bool Ready { get; private set; }
        public string? CurrentActor { get; private set; }
        public List<string> Actions { get; } = [];
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            action.ReportActiveActor(action.Command.Name);
            CurrentActor = action.Command.Name;
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command.Name + ":" + action.Command.Method.Alias[0]);
            if (BecomesReadyAfterSkill && action.Command.Method == Method.Skill) Ready = true;
            if (FailSkill && action.Command.Method == Method.Skill) return ValueTask.FromResult(CombatFlowResult.Failed);
            return ValueTask.FromResult(action.Command.Method == Method.Burst && !Ready
                ? CombatFlowResult.Skipped : CombatFlowResult.Succeeded);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => function switch
        {
            "q-ready" => Ready,
            "q-energy-low" => EnergyLow,
            "q-cd" => Cooling,
            "e-ready" => true,
            "q-energy-sample" => ReportEnergy && Context != null
                ? new CombatResourceSample(Context.BattleId, actor, Guid.NewGuid(), Context.Now, 0.25) : null,
            "onfield" => CurrentActor == args.FirstOrDefault()?.ToString(),
            _ => null
        };
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
