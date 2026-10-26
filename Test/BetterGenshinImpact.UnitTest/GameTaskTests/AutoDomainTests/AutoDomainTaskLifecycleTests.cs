using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomainTests;

public class AutoDomainTaskLifecycleTests
{
    [Fact]
    public async Task RunningDetectorCompletion_ShouldAwaitCombatExitAndDisposeSession()
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var combatStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var combatStopped = false;
        CancellationTokenSource? session = null;

        await AutoDomainTask.RunComboFightAsync(parent.Token, async ct =>
        {
            combatStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { combatStopped = true; }
        }, cts =>
        {
            session = cts;
            // 生产结束检测同样返回已启动的 Task.Run，宿主不得再次 Start。
            return Task.Run(async () =>
            {
                await combatStarted.Task.WaitAsync(cts.Token);
                await cts.CancelAsync();
            });
        });

        Assert.True(combatStopped);
        Assert.False(parent.IsCancellationRequested);
        AssertSessionDisposed(session);
        await parent.CancelAsync();
    }

    [Fact]
    public async Task CombatCompletion_ShouldCancelAndAwaitDetector()
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var detectorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detectorStopped = false;
        CancellationTokenSource? session = null;

        await AutoDomainTask.RunComboFightAsync(parent.Token,
            ct => detectorStarted.Task.WaitAsync(ct), async cts =>
            {
                session = cts;
                detectorStarted.SetResult();
                try { await Task.Delay(Timeout.Infinite, cts.Token); }
                finally { detectorStopped = true; }
            });

        Assert.True(detectorStopped);
        Assert.False(parent.IsCancellationRequested);
        AssertSessionDisposed(session);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CombatFailure_ShouldPreserveOriginalExceptionAfterDetectorExit(bool retry)
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var detectorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detectorStopped = false;
        Exception expected = retry
            ? new RetryException("检测到秘境内复苏界面，退出秘境后重试")
            : new InvalidOperationException("combat failed");
        CancellationTokenSource? session = null;

        var run = AutoDomainTask.RunComboFightAsync(parent.Token, async ct =>
        {
            await detectorStarted.Task.WaitAsync(ct);
            throw expected;
        }, async cts =>
        {
            session = cts;
            detectorStarted.SetResult();
            try { await Task.Delay(Timeout.Infinite, cts.Token); }
            finally { detectorStopped = true; }
        });

        Assert.Same(expected, await Record.ExceptionAsync(() => run));
        Assert.True(detectorStopped);
        Assert.False(parent.IsCancellationRequested);
        AssertSessionDisposed(session);
    }

    [Fact]
    public async Task ParentCancellation_ShouldAwaitBothChildrenAndRemainParentCancellation()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var parent = new CancellationTokenSource();
        var combatStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detectorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = 0;
        CancellationTokenSource? session = null;

        async Task RunChild(CancellationToken ct, TaskCompletionSource started)
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { Interlocked.Increment(ref stopped); }
        }

        var run = AutoDomainTask.RunComboFightAsync(parent.Token,
            ct => RunChild(ct, combatStarted), cts =>
            {
                session = cts;
                return RunChild(cts.Token, detectorStarted);
            });
        try
        {
            await Task.WhenAll(combatStarted.Task, detectorStarted.Task).WaitAsync(deadline.Token);
        }
        finally
        {
            await parent.CancelAsync();
        }

        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(deadline.Token));
        Assert.Equal(parent.Token, actual.CancellationToken);
        Assert.Equal(2, stopped);
        AssertSessionDisposed(session);
    }

    [Fact]
    public async Task PreCancelledParent_ShouldNotInvokeCombatOrDetector()
    {
        using var parent = new CancellationTokenSource();
        await parent.CancelAsync();
        var calls = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => AutoDomainTask.RunComboFightAsync(parent.Token,
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; }));

        Assert.Equal(0, calls);
    }

    private static void AssertSessionDisposed(CancellationTokenSource? session)
    {
        Assert.NotNull(session);
        Assert.Throws<ObjectDisposedException>(() => { _ = session.Token; });
    }
}
