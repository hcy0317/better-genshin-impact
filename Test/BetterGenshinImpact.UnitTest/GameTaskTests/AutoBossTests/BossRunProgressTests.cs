using BetterGenshinImpact.GameTask.AutoBoss;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoBossTests;

public class BossRunProgressTests
{
    [Fact]
    public async Task RetryAfterConfirmedClaimDoesNotConsumeThatShareAgain()
    {
        var progress = new BossRunProgress(2, 1);
        var claims = 0;
        await progress.RunAsync(() => Task.CompletedTask, () =>
        {
            progress.BeginClaim();
            claims++;
            progress.ConfirmClaim();
            if (claims == 1) throw new RetryException("确认领奖后恢复场景失败");
            return Task.FromResult(true);
        }, () => Task.CompletedTask, (_, _) => Task.CompletedTask, default);
        Assert.Equal(2, claims);
        Assert.Equal(2, progress.CompletedClaims);
    }

    [Fact]
    public async Task UncertainConsumptionCannotBeRetriedAsAnUnclaimedRound()
    {
        var progress = new BossRunProgress(2, 3);
        var inputs = 0;
        await Assert.ThrowsAsync<BossRewardUncertainException>(() => progress.RunAsync(() => Task.CompletedTask, () =>
        {
            progress.BeginClaim();
            inputs++;
            throw new RetryException("使用树脂之后未能确认奖励页");
        }, () => Task.CompletedTask, (_, _) => throw new InvalidOperationException("禁止自动再次消费"), default));
        Assert.Equal(1, inputs);
        Assert.Equal(0, progress.CompletedClaims);
        Assert.True(progress.ClaimPending);
    }

    [Fact]
    public async Task CancelledRunNeverStartsOrClearsConfirmedProgress()
    {
        var progress = new BossRunProgress(2, 1);
        progress.BeginClaim();
        progress.ConfirmClaim();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => progress.RunAsync(
            () => throw new InvalidOperationException("不能准备"), () => Task.FromResult(false),
            () => Task.CompletedTask, (_, _) => Task.CompletedTask, cancellation.Token));
        Assert.Equal(1, progress.CompletedClaims);
    }

    [Fact]
    public async Task SecondRoundRetryDoesNotRepeatTheFirstClaim()
    {
        var progress = new BossRunProgress(2, 1);
        var rounds = 0;
        var claims = 0;
        var preparations = 0;
        await progress.RunAsync(
            () => { preparations++; return Task.CompletedTask; },
            () =>
            {
                if (++rounds == 2) throw new RetryException("第二轮战斗失败");
                progress.BeginClaim();
                claims++;
                progress.ConfirmClaim();
                return Task.FromResult(true);
            }, () => Task.CompletedTask, (_, _) => Task.CompletedTask, default);
        Assert.Equal(2, claims);
        Assert.Equal(2, progress.CompletedClaims);
        Assert.Equal(3, rounds);
        Assert.Equal(2, preparations);
    }
}
