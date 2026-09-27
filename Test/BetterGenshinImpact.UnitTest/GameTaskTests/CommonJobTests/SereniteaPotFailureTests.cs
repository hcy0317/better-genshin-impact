using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class SereniteaPotFailureTests
{
    [Fact]
    public async Task SuccessfulCleanupDoesNotTurnAFailedVisitIntoSuccess()
    {
        var calls = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            GoToSereniteaPotTask.FailAfterCleanup("entry failed", _ =>
            {
                calls++;
                return Task.CompletedTask;
            }, default));
        Assert.Equal("entry failed", error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task CleanupFailureRetainsBothCauses()
    {
        var cleanupError = new IOException("cleanup failed");
        var error = await Assert.ThrowsAsync<AggregateException>(() =>
            GoToSereniteaPotTask.FailAfterCleanup("search failed", _ => Task.FromException(cleanupError), default));
        Assert.Equal("search failed", error.InnerExceptions[0].Message);
        Assert.Same(cleanupError, error.InnerExceptions[1]);
    }

    [Fact]
    public async Task CancellationDoesNotStartCleanupOrBecomeABusinessFailure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GoToSereniteaPotTask.FailAfterCleanup("entry failed", _ => throw new Exception("must not run"), cancellation.Token));
    }

    [Fact]
    public async Task CancellationDuringCleanupIsPreserved()
    {
        var cancellation = new OperationCanceledException();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GoToSereniteaPotTask.FailAfterCleanup("entry failed", _ => Task.FromException(cancellation), default));
        Assert.Same(cancellation, error);
    }
}
