using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatActionRecoveryScopeTests
{
    [Fact]
    public async Task DelayedWakeupsDoNotAccumulateIntoAFalseActionTimeout()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        using var scope = new CombatActionScope(new CombatFlowAction(
            new CombatCommand(CombatScriptParser.CurrentAvatarName, "wait(6.5)"), context, () => true, 8), default);

        await scope.WaitAsync(6500, (milliseconds, _) =>
        {
            // 外部调度每次迟到12.5ms；不是用被测实现推导期望时间。
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds + 12.5));
            return Task.CompletedTask;
        });

        Assert.InRange(context.Now, 6.5, 6.563);
        scope.Check();
    }

    [Fact]
    public async Task EarlyWakeupsStillWaitForTheRequestedElapsedDuration()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        using var scope = new CombatActionScope(new CombatFlowAction(
            new CombatCommand("琴", "wait(1)"), context, () => true, 8), default);
        await scope.WaitAsync(1000, (milliseconds, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(Math.Max(.5, milliseconds / 2d)));
            return Task.CompletedTask;
        });
        Assert.InRange(context.Now, 1, 1.001);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARealDeadlineOrCancellationStillInterruptsTheOriginalWait(bool cancelled)
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var context = new CombatFlowContext(clock);
        using var scope = new CombatActionScope(new CombatFlowAction(
            new CombatCommand("琴", "wait(1)"), context, () => true, .2), cancellation.Token);
        var failure = await Record.ExceptionAsync(() => scope.WaitAsync(1000, (_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(1800));
            if (cancelled) cancellation.Cancel();
            return Task.CompletedTask;
        }));
        if (cancelled) Assert.IsType<OperationCanceledException>(failure);
        else Assert.IsType<CombatActionInterruptedException>(failure);
    }

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
