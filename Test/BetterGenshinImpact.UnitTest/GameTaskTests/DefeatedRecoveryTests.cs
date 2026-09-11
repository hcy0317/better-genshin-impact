using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class DefeatedRecoveryTests
{
    [Fact]
    public async Task CancellationBeforeRevivalCannotTeleport()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var clock = new FakeTimeProvider();
        var teleported = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => UiRecovery.RecoverDefeatedAsync(
            new DefeatDriver(clock, true), _ => { teleported = true; return Task.CompletedTask; },
            cancellation.Token, clock: clock));
        Assert.False(teleported);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StatueRecoveryRequiresConfirmedHud(bool recovered)
    {
        var clock = new FakeTimeProvider();
        var driver = new DefeatDriver(clock, recovered);
        var teleported = false;
        var work = UiRecovery.RecoverDefeatedAsync(driver, _ =>
        {
            Assert.True(driver.HudSamples >= 2);
            teleported = true;
            return Task.CompletedTask;
        }, default, clock: clock);
        if (recovered) await Assert.ThrowsAsync<DefeatedRetryException>(() => work);
        else await Assert.ThrowsAsync<TimeoutException>(() => work);
        Assert.Equal(recovered, teleported);
    }

    private sealed class DefeatDriver(FakeTimeProvider clock, bool recovered) : IUiDriver
    {
        private long _frame;
        private bool _clicked;
        internal int HudSamples;
        public UiSnapshot Capture()
        {
            if (_clicked && recovered)
            {
                HudSamples++;
                return new(++_frame) { MainHud = true };
            }
            return new(++_frame) { Revive = true, FullPartyDefeat = true };
        }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        {
            Assert.Equal(UiAction.ReviveParty, action);
            _clicked = true;
            return Task.FromResult(true);
        }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void OnlyConfirmedDefeatCanRemainRetryableBeforeBattleEnd()
    {
        using var task = TaskExecutionScope.BeginOwned();
        var defeated = new DefeatedRetryException("已确认败北");
        Assert.Same(defeated, Assert.Throws<DefeatedRetryException>(() =>
            TaskExecutionScope.RethrowCombatFailure(defeated, true, false, default)));
        Assert.Null(TaskExecutionScope.Failure);
        Assert.Throws<CombatNotFinishedException>(() =>
            TaskExecutionScope.RethrowCombatFailure(new RetryException("普通失败"), true, false, default));
        Assert.NotNull(TaskExecutionScope.Failure);
    }
}
