using BetterGenshinImpact.GameTask.AutoDomain;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomainTests;

public class AutoDomainHandoffTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void FinalConfiguredRoundExitsEvenWithRemainingResin(bool resinLast, bool roundLast, bool expected) =>
        Assert.Equal(expected, AutoDomainTask.ShouldExitAfterDomainReward(resinLast, roundLast));

    [Fact]
    public async Task ExitIsAwaitedBeforeMainUiAndArtifactProcessing()
    {
        var exit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var task = AutoDomainTask.CompleteDomainHandoffAsync(
            () => { calls.Add("exit"); return exit.Task; },
            () => { calls.Add("main"); return Task.FromResult(true); },
            () => { calls.Add("artifacts"); return Task.CompletedTask; }, default);
        Assert.False(task.IsCompleted);
        Assert.Equal(new[] { "exit" }, calls);
        exit.SetResult(true);
        await task;
        Assert.Equal(new[] { "exit", "main", "artifacts" }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedConfirmationCannotProcessArtifacts(bool exitSucceeded)
    {
        var mainCalls = 0;
        var artifacts = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => AutoDomainTask.CompleteDomainHandoffAsync(
            () => Task.FromResult(exitSucceeded),
            () => { mainCalls++; return Task.FromResult(false); },
            () => { artifacts++; return Task.CompletedTask; }, default));
        Assert.Equal(exitSucceeded ? 1 : 0, mainCalls);
        Assert.Equal(0, artifacts);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task CancellationAtEitherBoundaryPreventsFurtherWork(int boundary)
    {
        using var cts = new CancellationTokenSource();
        var calls = new List<string>();
        if (boundary == 0) cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoDomainTask.CompleteDomainHandoffAsync(
            () => { calls.Add("exit"); if (boundary == 1) cts.Cancel(); return Task.FromResult(true); },
            () => { calls.Add("main"); if (boundary == 2) cts.Cancel(); return Task.FromResult(true); },
            () => { calls.Add("artifacts"); return Task.CompletedTask; }, cts.Token));
        Assert.Equal(boundary, calls.Count);
        Assert.DoesNotContain("artifacts", calls);
    }

    [Fact]
    public async Task ExitFailureIsPreservedWithoutRetryOrMainUiInput()
    {
        var failure = new TimeoutException("exit failed");
        var actual = await Assert.ThrowsAsync<TimeoutException>(() => AutoDomainTask.CompleteDomainHandoffAsync(
            () => throw failure, () => throw new Exception("unexpected main"),
            () => throw new Exception("unexpected artifacts"), default));
        Assert.Same(failure, actual);
    }

    [Fact]
    public async Task CancellationDuringArtifactProcessingCannotReportSuccess()
    {
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoDomainTask.CompleteDomainHandoffAsync(
            () => Task.FromResult(true), () => Task.FromResult(true),
            () => { cts.Cancel(); return Task.CompletedTask; }, cts.Token));
    }
}
