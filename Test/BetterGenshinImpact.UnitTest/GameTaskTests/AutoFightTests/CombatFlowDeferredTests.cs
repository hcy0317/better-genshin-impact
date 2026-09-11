using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowDeferredTests
{
    [Fact]
    public async Task WaitingForMaintainedSkillDoesNotSpendNewAttemptsOrResetItsDeadline()
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock) { ConfirmCooldown = false };
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            timing(护盾时长,duration=20)
            钟离 e(required,record=护盾,timing=护盾时长,maintain=护盾)
            """), game, clock);
        for (var pass = 0; pass < 5; pass++)
            Assert.Equal(CombatFlowResult.Deferred, await execution.RunRoundAsync());
        clock.Advance(TimeSpan.FromSeconds(15));
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Null(execution.Context.Find("护盾"));
        Assert.Equal(1, game.Inputs);
    }

    [Fact]
    public async Task PendingBurstIsConfirmedBeforeTheRechargeCooldownGate()
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            香菱 q(recharge,from=班尼特,required,record=火轮)
            segment(供能,define) { 班尼特 e(feed=香菱) }
            """), game, clock);
        Assert.Equal(CombatFlowResult.Deferred, await execution.RunRoundAsync());
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(0, execution.Context.Find("火轮")!.OccurredAt);
        Assert.Equal(1, game.Inputs);
    }

    [Fact]
    public async Task LateNativeConfirmationRecordsOriginalTimeAndOnlyOnePhysicalInput()
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock);
        using var execution = new CombatFlowExecution(
            CombatFlowProgram.Compile("timing(窗口,duration=10)\n那维莱特 e(required,record=水滴,timing=窗口)"), game, clock);
        Assert.Equal(CombatFlowResult.Deferred, await execution.RunRoundAsync());
        Assert.Null(execution.Context.Find("水滴"));
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(0, execution.Context.Find("水滴")!.OccurredAt);
        Assert.Equal(1, game.Inputs);
        Assert.Single(execution.RuntimeStatistics.RecentEvents.Where(item => item.InputStarted));
    }

    private sealed class PendingGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        private CombatSkillAttempts? _attempts;
        private long _frame;
        public int Inputs;
        public bool ConfirmCooldown { get; init; } = true;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            _attempts ??= new(action.BattleId);
            var pending = NativeCombatFlowRunner.ReconcilePendingSkill(_attempts, action, action.Command.Name,
                () => new(action.BattleId, ++_frame, action.Now, ConfirmCooldown ? true : null, ConfirmCooldown ? false : null));
            if (pending != null) return ValueTask.FromResult(pending.Value);
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Inputs++;
            _attempts.TryBegin(action.Command.Name, action.Command.Method, action.CommandId,
                action.InputAt!.Value, action.Now + action.RemainingBudget);
            clock.Advance(TimeSpan.FromSeconds(1));
            return ValueTask.FromResult(CombatFlowResult.Pending);
        }
        public bool HasPendingSkill(CombatFlowAction action) =>
            _attempts?.HasUnresolved(action.Command.Name, action.Command.Method) == true;
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => function switch
        {
            "q-ready" => Inputs == 0, "q-cd" => Inputs > 0, "q-energy-low" => Inputs > 0, _ => null
        };
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
