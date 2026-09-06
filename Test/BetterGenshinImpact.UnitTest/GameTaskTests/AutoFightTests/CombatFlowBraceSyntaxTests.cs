using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowBraceSyntaxTests
{
    [Theory]
    [InlineData("segment(就地片段) { 琴 wait(0.1) }", 1)]
    [InlineData("segment(延后片段,define) { 琴 wait(0.1) }", 0)]
    [InlineData("segment(外层) { segment(内层) { 琴 wait(0.1) } }", 1)]
    [InlineData("segment(分行)\n{\n琴 wait(0.1)\n}", 1)]
    [InlineData("segment(引号，define){ record('窗口{甲}',duration=8) } call(引号)", 0)]
    public async Task DefineDefersExecutionWhilePlainAndNestedBlocksRunInPlace(string script, int actions)
    {
        var clock = new FakeTimeProvider();
        var game = new ScriptGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile(script), game, clock);
        await execution.RunRoundAsync();
        Assert.Equal(actions, game.Actions);
    }

    [Theory]
    [InlineData("{ 琴 wait(0.1) }")]
    [InlineData("segment(开场,define) { 琴 wait(0.1)")]
    [InlineData("segment(开场,define)\n琴 wait(0.1)")]
    [InlineData("segment(开场,define) { segment(end) }")]
    [InlineData("segment(开场,define) { 琴 wait(0.1) } }")]
    public void UnbalancedOrMixedBlockTerminatorsAreRejectedAtTheirSource(string script)
    {
        var error = Assert.Throws<FormatException>(() => CombatFlowProgram.Compile(script));
        Assert.Contains("行", error.Message);
        Assert.Contains("列", error.Message);
    }

    [Fact]
    public async Task JsonActionUsesTheSameBraceSyntaxAndDefaultActor()
    {
        var clock = new FakeTimeProvider();
        var game = new ScriptGame(clock);
        using var execution = new JsonCombatFlowExecution(new() { Actions = [new()
        {
            Character = "琴", Action = "segment(准备，define)｛ wait(0.1,record=已准备) ｝，call(准备)"
        }] }, game, clock: clock);
        for (var step = 0; step < 10 && execution.Context.Find("已准备") == null; step++) await execution.StepAsync();
        Assert.NotNull(execution.Context.Find("已准备"));
        Assert.Equal(new[] { "琴" }, execution.Actors);
        Assert.Equal(1, game.Actions);
    }

    [Fact]
    public async Task NamedBraceDefinitionRunsOnlyWhenCalledAndRecordsCompletionAfterItsBody()
    {
        var program = CombatFlowProgram.Compile("""
            segment(开场，define，record=开场完成) {
                琴 wait(0.1,record=片段动作)
            }
            record(定义之后)
            call(开场,once=battle,required)
            """);
        var clock = new FakeTimeProvider();
        var game = new ScriptGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.RunRoundAsync();
        Assert.Equal(1, game.Actions);
        Assert.True(execution.Context.Find("定义之后")!.Generation < execution.Context.Find("片段动作")!.Generation);
        Assert.True(execution.Context.Find("片段动作")!.Generation < execution.Context.Find("开场完成")!.Generation);
        await execution.RunRoundAsync();
        Assert.Equal(1, game.Actions);
    }

    private sealed class ScriptGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public int Actions;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions++;
            clock.Advance(TimeSpan.FromSeconds(.1));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
