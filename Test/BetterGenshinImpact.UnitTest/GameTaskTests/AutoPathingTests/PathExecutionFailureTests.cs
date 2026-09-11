using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathExecutionFailureTests
{
    [Fact]
    public async Task ExhaustedRetryCannotContinueToTheNextSegmentOrReportRouteSuccess()
    {
        var attempts = 0;
        var releases = 0;
        var continued = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await PathExecutor.ExecuteSegmentWithRetriesAsync(() =>
            {
                attempts++;
                throw new RetryException("未完成战斗点");
            }, _ => { }, () => releases++, default);
            continued = true;
        });
        Assert.Equal(2, attempts);
        Assert.Equal(2, releases);
        Assert.False(continued);
        Assert.IsType<RetryException>(error.InnerException);
    }

    [Fact]
    public async Task RetryThenSuccessKeepsNormalCompletionAndAlwaysReleasesInput()
    {
        var attempts = 0;
        var releases = 0;
        var endedEarly = await PathExecutor.ExecuteSegmentWithRetriesAsync(() =>
        {
            if (++attempts == 1) throw new RetryException("定位暂不可用");
            return Task.CompletedTask;
        }, _ => { }, () => releases++, default);
        Assert.False(endedEarly);
        Assert.Equal(2, attempts);
        Assert.Equal(2, releases);
    }

    [Fact]
    public async Task OnlyExplicitEndConditionMayCompleteARouteEarly()
    {
        Assert.True(await PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => throw new PathExecutor.EndConditionSatisfiedException(), _ => { }, () => { }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => throw new HandledException("无法识别点位，放弃路径"), _ => { }, () => { }, default));
    }

    [Fact]
    public async Task CancellationAndCombatExecutionFailureCannotBecomeSuccessfulRoutes()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => throw new Exception("取消后不得执行路径"), _ => { }, () => { }, cts.Token));
        var expected = new InvalidOperationException("战斗未确认结束");
        var retries = 0;
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => throw expected, _ => retries++, () => { }, default));
        Assert.Same(expected, actual);
        Assert.Equal(0, retries);
    }
}
