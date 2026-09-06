using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowWaitTests
{
    [Fact]
    public async Task ZeroLengthWaitsAndSelfGeneratedRecordsDoNotSuppressTheIdleYield()
    {
        var game = new EmptyWaitGame();
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("strategy(loop=battle)\n琴 wait(0,record=标记)"), game, new FakeTimeProvider());
        await execution.RunRoundAsync();
        Assert.NotNull(execution.Context.Find("标记"));
        Assert.Equal(1, game.Yields);
    }

    [Fact]
    public async Task LongWaitYieldsToDueMaintenanceWithoutWritingACompletionRecord()
    {
        var program = CombatFlowProgram.Compile(CombatScriptParser.ParseContext("""
            timing(窗口,duration=1)
            琴 e(timing=窗口,record=效果,watch=效果,before=0.5)
            琴 wait(10,record=等待结束)
            琴 e(timing=窗口,record=效果,watch=效果,before=0.5)
            """));
        var clock = new FakeTimeProvider();
        var game = new WaitingGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        await execution.StepAsync();
        Assert.Null(execution.Context.Find("等待结束"));
        Assert.InRange(execution.Context.Now, .5, .6);
        await execution.StepAsync();
        Assert.Equal(2, game.Skills);
    }

    private sealed class EmptyWaitGame : ICombatFlowGame
    {
        public int Yields;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct) =>
            ValueTask.FromResult(action.TryBeginInput() ? CombatFlowResult.Succeeded : CombatFlowResult.Skipped);
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) { Yields++; return ValueTask.CompletedTask; }
    }

    private sealed class WaitingGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public int Skills;
        public async ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return CombatFlowResult.Skipped;
            if (action.Command.Method != Method.Wait) { Skills++; return CombatFlowResult.Succeeded; }
            using var scope = new CombatActionScope(action, ct);
            try
            {
                await scope.WaitAsync(10000, (milliseconds, _) =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                    return Task.CompletedTask;
                });
                return CombatFlowResult.Succeeded;
            }
            catch (CombatActionInterruptedException) { return CombatFlowResult.Skipped; }
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
