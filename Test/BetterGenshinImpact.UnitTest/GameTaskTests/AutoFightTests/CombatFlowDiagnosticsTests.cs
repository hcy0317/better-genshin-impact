using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowDiagnosticsTests
{
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
        Assert.True(statistics.CoreStepMilliseconds >= 0);
        execution.Dispose();
        Assert.Null(execution.Context.Find("动作"));
        using var next = new CombatFlowExecution(program, new DiagnosticGame(clock), clock);
        Assert.Equal(0, next.RuntimeStatistics.GameActionCalls);
        Assert.Empty(next.RuntimeStatistics.RecentEvents);
        Assert.Null(next.Context.Find("动作"));
    }

    private sealed class DiagnosticGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => true;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
