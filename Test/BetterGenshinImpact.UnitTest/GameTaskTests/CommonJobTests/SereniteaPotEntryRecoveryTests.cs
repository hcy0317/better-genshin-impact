using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class SereniteaPotEntryRecoveryTests
{
    [Theory]
    [InlineData("first")]
    [InlineData("recovery")]
    [InlineData("parent")]
    public async Task ExpiredEntryCannotStartAnotherRecoveryOrReentry(string phase)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var parent = phase == "parent" ? BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Begin(
            "test-parent", TimeSpan.FromSeconds(30), clock: clock) : null;
        var attempts = 0;
        var recoveries = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => SereniteaPotEntryRecovery.EnterAsync(_ =>
        {
            attempts++;
            clock.Advance(TimeSpan.FromSeconds(phase == "first" ? 241 : 20));
            throw new TeleportCombatBlockedException();
        }, _ =>
        {
            recoveries++;
            clock.Advance(TimeSpan.FromSeconds(phase == "parent" ? 11 : 221));
            return Task.CompletedTask;
        }, default, clock));
        Assert.Equal(1, attempts);
        Assert.Equal(phase == "first" ? 0 : 1, recoveries);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReentryConsumesOnlyTheRemainingSharedEntryBudget(bool overrun)
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var attempts = 0;
        var tokens = new List<CancellationToken>();
        var remaining = new List<TimeSpan>();
        async Task Run()
        {
            Assert.True(await SereniteaPotEntryRecovery.EnterAsync(token =>
            {
                tokens.Add(token);
                remaining.Add(BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Current?.Remaining ?? TimeSpan.MaxValue);
                clock.Advance(TimeSpan.FromSeconds(++attempts == 1 ? 60 : overrun ? 110 : 70));
                if (attempts == 1) throw new TeleportCombatBlockedException();
                return Task.FromResult(true);
            }, token =>
            {
                tokens.Add(token);
                clock.Advance(TimeSpan.FromSeconds(80));
                return Task.CompletedTask;
            }, default, clock));
        }
        if (overrun) await Assert.ThrowsAsync<TimeoutException>(Run);
        else await Run();
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { TimeSpan.FromSeconds(240), TimeSpan.FromSeconds(100) }, remaining);
        Assert.All(tokens, token => Assert.Equal(tokens[0], token));
        Assert.True(tokens[0].CanBeCanceled);
    }

    [Theory]
    [InlineData("rejected-again")]
    [InlineData("recovery-failed")]
    [InlineData("cancelled")]
    public async Task FailedOrCancelledRecoveryCannotContinueIndefinitely(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var recoveries = 0;
        var error = await Record.ExceptionAsync(() => SereniteaPotEntryRecovery.EnterAsync(_ =>
        {
            attempts++;
            throw new TeleportCombatBlockedException();
        }, _ =>
        {
            recoveries++;
            if (failure == "recovery-failed") throw new IOException("recovery failed");
            if (failure == "cancelled") cancellation.Cancel();
            return Task.CompletedTask;
        }, cancellation.Token));
        Assert.NotNull(error);
        Assert.Equal(1, recoveries);
        Assert.Equal(failure == "rejected-again" ? 2 : 1, attempts);
        if (failure == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    [Fact]
    public async Task ExplicitCombatRejectionLeavesCombatThenReentersExactlyOnce()
    {
        var attempts = 0;
        var leftCombat = false;
        var result = await SereniteaPotEntryRecovery.EnterAsync(_ =>
        {
            if (++attempts == 1) throw new TeleportCombatBlockedException();
            Assert.True(leftCombat);
            return Task.FromResult(true);
        }, _ => { leftCombat = true; return Task.CompletedTask; }, default);
        Assert.True(result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task UnconfirmedInputDoesNotAuthorizeRelocationOrRetry()
    {
        var original = new TeleportConfirmationUnconfirmedException();
        Assert.Same(original, await Assert.ThrowsAsync<TeleportConfirmationUnconfirmedException>(() =>
            SereniteaPotEntryRecovery.EnterAsync(_ => throw original,
                _ => throw new Exception("must not relocate"), default)));
    }
}
