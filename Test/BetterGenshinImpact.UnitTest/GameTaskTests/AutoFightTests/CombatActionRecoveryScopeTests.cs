using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatActionRecoveryScopeTests
{
    [Fact]
    public void RecoveryCanOutliveActionBudgetButRestoresItEvenWhenRecoveryFails()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        using var scope = new CombatActionScope(new CombatFlowAction(
            new CombatCommand("琴", "wait(1)"), context, () => true, .1), default);
        Assert.Throws<TimeoutException>((Action)(() =>
        {
            using var recovery = CombatActionScope.Suspend();
            Assert.Null(CombatActionScope.Current);
            clock.Advance(TimeSpan.FromSeconds(2));
            using (CombatActionScope.Suspend()) Assert.Null(CombatActionScope.Current);
            throw new TimeoutException("恢复自身超时");
        }));
        Assert.Same(scope, CombatActionScope.Current);
        Assert.Throws<CombatActionInterruptedException>(scope.Check);
    }

    [Fact]
    public void RecoveryRetainsLegacyCancellationWithoutInvokingGameInput()
    {
        using var ct = new CancellationTokenSource();
        using var context = new CombatFlowContext();
        using var scope = new CombatActionScope(new CombatFlowAction(
            new CombatCommand("琴", "wait(1)"), context, () => true, 10), ct.Token);
        using (CombatActionScope.Suspend())
        {
            ct.Cancel();
            Assert.Throws<NormalEndException>(() => TaskControl.Sleep(0, ct.Token));
        }
        Assert.Same(scope, CombatActionScope.Current);
        Assert.Throws<OperationCanceledException>(scope.Check);
    }
}
