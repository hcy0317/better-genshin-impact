using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class CountInventoryScanBudgetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimeoutWaitsForScannerCleanupAndUnderstandsLegacyCancellation(bool legacyCancellation)
    {
        var cleaned = false;
        await Assert.ThrowsAsync<TimeoutException>(() => CountInventoryItem.RunWithTimeout<int>(async token =>
        {
            try
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) when (legacyCancellation) { throw new NormalEndException("取消自动任务"); }
                return 1;
            }
            finally { cleaned = true; }
        }, default, TimeSpan.FromMilliseconds(25)));
        Assert.True(cleaned);
    }

    [Fact]
    public async Task LateSuccessIsNotAcceptedAfterBudgetCancellation()
    {
        var exited = false;
        await Assert.ThrowsAsync<TimeoutException>(() => CountInventoryItem.RunWithTimeout(async _ =>
        {
            await Task.Delay(60);
            exited = true;
            return 42;
        }, default, TimeSpan.FromMilliseconds(10)));
        Assert.True(exited);
    }

    [Fact]
    public async Task ParentCancellationIsNotMisreportedAsTimeoutAndPreCancellationDoesNotStartScanner()
    {
        using var parent = new CancellationTokenSource();
        parent.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CountInventoryItem.RunWithTimeout<int>(
            _ => throw new Exception("预取消后不得启动扫描"), parent.Token));
        using var activeParent = new CancellationTokenSource();
        await Assert.ThrowsAsync<NormalEndException>(() => CountInventoryItem.RunWithTimeout<int>(_ =>
        {
            activeParent.Cancel();
            throw new NormalEndException("取消自动任务");
        }, activeParent.Token));
    }

    [Fact]
    public async Task SuccessfulScanKeepsResultAndRealFaultAfterDeadlineIsNotHidden()
    {
        Assert.Equal(42, await CountInventoryItem.RunWithTimeout(_ => Task.FromResult(42), default));
        var expected = new InvalidOperationException("真实识别错误");
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => CountInventoryItem.RunWithTimeout<int>(async _ =>
        {
            await Task.Delay(60);
            throw expected;
        }, default, TimeSpan.FromMilliseconds(10)));
        Assert.Same(expected, actual);
    }
}
