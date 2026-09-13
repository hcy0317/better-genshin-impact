using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class ApplicationShutdownSequenceTests
{
    [Fact]
    public async Task ReentrantClosingSharesOneRequestAndClosesOnlyOnce()
    {
        ApplicationShutdownSequence? sequence = null;
        Task? nested = null;
        var drains = 0;
        var closes = 0;
        sequence = new(() =>
        {
            if (++drains == 1) nested = sequence!.RequestAsync();
            return Task.CompletedTask;
        }, () => closes++);
        var stopped = sequence.RequestAsync();
        await stopped;
        Assert.Same(stopped, nested);
        Assert.Equal(1, drains);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task ClosingWaitsForDrainWithoutBlockingTheUiCallerAndRunsOnlyOnce()
    {
        var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var closed = 0;
        var shutdown = new ApplicationShutdownSequence(() => drained.Task, () => closed++);
        var pending = shutdown.RequestAsync();
        Assert.False(pending.IsCompleted);
        Assert.False(shutdown.Prepared);
        Assert.Equal(0, closed);
        drained.SetResult();
        await pending;
        await shutdown.RequestAsync();
        Assert.True(shutdown.Prepared);
        Assert.Equal(1, closed);
    }

    [Fact]
    public async Task DrainFailureCannotProceedToDisposeLiveServices()
    {
        var closed = false;
        var shutdown = new ApplicationShutdownSequence(() => Task.FromException(new InvalidOperationException("drain failed")), () => closed = true);
        await Assert.ThrowsAsync<InvalidOperationException>(shutdown.RequestAsync);
        Assert.False(closed);
        Assert.False(shutdown.Prepared);
    }
}
