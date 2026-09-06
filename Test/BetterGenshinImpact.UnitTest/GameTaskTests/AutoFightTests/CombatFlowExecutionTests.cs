using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowExecutionTests
{
    [Fact]
    public async Task CompiledBattleDoesNotObserveLaterEditsToSourceCommands()
    {
        var script = CombatScriptParser.ParseContext("钟离 e(record=护盾)");
        var program = CombatFlowProgram.Compile(script);
        script.CombatCommands[0].Name = "琴";
        script.CombatCommands[0].Options["record"] = "另一记录";
        var game = new FakeGame();
        using var execution = new CombatFlowExecution(program, game);
        await execution.RunRoundAsync();
        Assert.Equal("钟离", Assert.Single(game.Actions));
        Assert.NotNull(execution.Context.Find("护盾"));
        Assert.Null(execution.Context.Find("另一记录"));
    }

    [Fact]
    public async Task WatchSkipsDisabledSuccessorWithoutSpinningThroughRounds()
    {
        var program = CombatFlowProgram.Compile("""
            strategy(loop=battle)
            timing(窗口,duration=20)
            钟离 e(record=覆盖,timing=窗口,maintain=覆盖,watch=覆盖)
            班尼特 e(record=覆盖,timing=窗口,maintain=覆盖,watch=覆盖,if=e-ready(班尼特))
            """);
        var clock = new FakeTimeProvider();
        var game = new FakeGame { Ready = false };
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        await execution.StepAsync();
        Assert.Equal(new[] { "钟离", "钟离" }, game.Actions);
        Assert.Equal(2, execution.Round);
    }

    [Fact]
    public async Task FailedOrPrematurelyReturnedOpeningNeverUnlocksLaterCommands()
    {
        var program = CombatFlowProgram.Compile("""
            call(开场,once=battle,required)
            琴 attack(1)
            segment(start,name=开场,define)
            return
            钟离 e(required)
            segment(end)
            """);
        var game = new FakeGame();
        using var execution = new CombatFlowExecution(program, game);
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Empty(game.Actions);
    }

    [Fact]
    public async Task OddEvenRoundFiltersUseRootRoundsNotSubroutineCalls()
    {
        var program = CombatFlowProgram.Compile("""
            钟离 round(odd), e
            琴 round(even), attack(0.1)
            """);
        var game = new FakeGame();
        using var execution = new CombatFlowExecution(program, game);
        await execution.RunRoundAsync();
        await execution.RunRoundAsync();
        await execution.RunRoundAsync();
        Assert.Equal(new[] { "钟离", "琴", "钟离" }, game.Actions);
    }

    [Fact]
    public async Task ExpiredNamedWatchAdvancesToNextProducerThenWrapsInsideSameBattle()
    {
        var program = CombatFlowProgram.Compile("""
            strategy(loop=battle)
            timing(窗口时长,duration=20)
            钟离 e(hold,record=护盾,timing=窗口时长,maintain=护盾,watch=护盾)
            琴 attack(0.1)
            班尼特 q(record=护盾,timing=窗口时长,maintain=护盾,watch=护盾)
            琴 attack(0.1)
            """);
        var clock = new FakeTimeProvider();
        var game = new FakeGame();
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync(); // 第一个具名生成端
        clock.Advance(TimeSpan.FromSeconds(20));
        await execution.StepAsync(); // 跳过输出，进入下一个生成端
        clock.Advance(TimeSpan.FromSeconds(20));
        await execution.StepAsync(); // 根循环回到第一个生成端
        Assert.Equal(new[] { "钟离", "班尼特", "钟离" }, game.Actions);
        Assert.Equal(2, execution.Round);
    }

    [Fact]
    public async Task MaintainingAnExistingWindowDoesNotCastOrRenewItsTimestamp()
    {
        var program = CombatFlowProgram.Compile("""
            timing(护盾时长,duration=20)
            钟离 e(hold,record=护盾,timing=护盾时长,maintain=护盾,before=4,required)
            琴 attack(0.1,keep=护盾)
            """);
        var clock = new FakeTimeProvider();
        var game = new FakeGame();
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.RunRoundAsync();
        var first = execution.Context.Find("护盾")!;
        clock.Advance(TimeSpan.FromSeconds(10));
        await execution.RunRoundAsync();
        Assert.Equal(new[] { "钟离", "琴", "琴" }, game.Actions);
        Assert.Equal(first, execution.Context.Find("护盾"));
        clock.Advance(TimeSpan.FromSeconds(7));
        await execution.RunRoundAsync();
        Assert.Equal(new[] { "钟离", "琴", "琴", "钟离", "琴" }, game.Actions);
        Assert.True(execution.Context.Find("护盾")!.Generation > first.Generation);
    }

    [Theory]
    [InlineData(true, "班尼特")]
    [InlineData(false, "琴")]
    [InlineData(null, "钟离")]
    public async Task BranchSelectsExactlyOnePathAndThenReturns(bool? ready, string expected)
    {
        var program = CombatFlowProgram.Compile("""
            branch(if=e-ready(班尼特),then=供能,else=短打,unknown=复查)
            segment(start,name=供能,define)
            班尼特 e
            segment(end)
            segment(start,name=短打,define)
            琴 attack(0.1)
            segment(end)
            segment(start,name=复查,define)
            钟离 wait(0.1)
            segment(end)
            """);
        var game = new FakeGame { Ready = ready };
        using var execution = new CombatFlowExecution(program, game);
        await execution.RunRoundAsync();
        Assert.Equal(expected, Assert.Single(game.Actions));
    }

    [Fact]
    public async Task OpeningIsConfirmedOnceAndSubroutineReturnsToCaller()
    {
        var program = CombatFlowProgram.Compile("""
            call(开场,once=battle,required)
            call(供能)
            琴 attack(0.1,record=输出)
            segment(start,name=开场,define)
            钟离 e(hold,required,record=护盾)
            segment(end)
            segment(start,name=供能,define)
            班尼特 e(required)
            segment(end)
            """);
        var game = new FakeGame();
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "钟离", "班尼特", "琴", "班尼特", "琴" }, game.Actions);
        Assert.NotNull(execution.Context.Find("输出"));
    }

    private sealed class FakeGame : ICombatFlowGame
    {
        public bool? Ready { get; init; }
        public List<string> Actions { get; } = [];
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            action.ReportActiveActor(action.Command.Name);
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command.Name);
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => Ready;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
