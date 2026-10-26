using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class TaskRunnerAdmissionTests
{
    [Fact]
    public async Task QueuedAdmissionInitializesCancellationOnlyAfterOwnershipTransfers()
    {
        using var semaphore = new SemaphoreSlim(0, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var initialized = 0;
        var waiting = TaskRunnerStartupGate.TryAcquireAsync(semaphore, () => initialized++,
            timeout.Token, waitForTask: true);

        Assert.False(waiting.IsCompleted);
        Assert.Equal(0, initialized);
        semaphore.Release();
        Assert.True(await waiting);
        Assert.Equal(1, initialized);
        Assert.Equal(0, semaphore.CurrentCount);
        semaphore.Release();
    }

    [Fact]
    public async Task CancellingQueuedAdmissionNeitherInitializesNorReleasesTheOwnersLock()
    {
        using var semaphore = new SemaphoreSlim(0, 1);
        using var cancelled = new CancellationTokenSource();
        var initialized = false;
        var waiting = TaskRunnerStartupGate.TryAcquireAsync(semaphore, () => initialized = true,
            cancelled.Token, waitForTask: true);

        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(initialized);
        Assert.Equal(0, semaphore.CurrentCount);
        semaphore.Release();
        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public async Task CancellationAtInitializationBoundaryReturnsTheAcquiredLock()
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var cancelled = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TaskRunnerStartupGate.TryAcquireAsync(semaphore, cancelled.Cancel,
                cancelled.Token, waitForTask: true));

        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public async Task AlreadyCancelledAdmissionDoesNotInitializeOrTakeAvailableLock()
    {
        using var semaphore = new SemaphoreSlim(1, 1);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var initialized = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            TaskRunnerStartupGate.TryAcquireAsync(semaphore, () => initialized = true,
                cancelled.Token, waitForTask: true));

        Assert.False(initialized);
        Assert.Equal(1, semaphore.CurrentCount);
    }

    [Fact]
    public async Task AnotherAutomationRunRetainsOwnershipBetweenItsInputTasks()
    {
        var cancellation = new CancellationContext();
        using var semaphore = new SemaphoreSlim(1, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var rejectedWhileRunActive = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialized = false;
        var run = cancellation.EnterRun();
        var ownerToken = cancellation.GetTokenOrNone();
        Task<bool> waiting;
        using (ExecutionContext.SuppressFlow())
        {
            waiting = Task.Run(() => TaskRunnerStartupGate.TryAcquireAsync(semaphore, () => initialized = true,
                timeout.Token, waitForTask: true, canEnter: () =>
                {
                    var available = TaskRunnerStartupGate.CanEnterRun(cancellation);
                    if (!available) rejectedWhileRunActive.TrySetResult();
                    return available;
                }));
        }
        try
        {
            await rejectedWhileRunActive.Task.WaitAsync(timeout.Token);
            Assert.False(initialized);
            Assert.False(waiting.IsCompleted);
            Assert.False(ownerToken.IsCancellationRequested);
            // 队列不能长期占用输入锁，否则一条龙无法执行后续子任务。
            Assert.True(await semaphore.WaitAsync(1000, timeout.Token));
            semaphore.Release();
        }
        finally
        {
            await run.DisposeAsync();
        }

        Assert.True(await waiting);
        Assert.True(initialized);
        semaphore.Release();
    }

    [Fact]
    public async Task ClosingAdmissionCancelsWaitWithoutReleasingTheRunningTasksLock()
    {
        using var semaphore = new SemaphoreSlim(0, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var closing = 0;
        var checking = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initialized = false;
        var waiting = TaskRunnerStartupGate.TryAcquireAsync(semaphore, () => initialized = true,
            timeout.Token, waitForTask: true, checkWaiting: () =>
            {
                checking.TrySetResult();
                if (Volatile.Read(ref closing) != 0) throw new OperationCanceledException("closing");
            });

        await checking.Task.WaitAsync(timeout.Token);
        Volatile.Write(ref closing, 1);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.False(initialized);
        Assert.Equal(0, semaphore.CurrentCount);
        semaphore.Release();
    }
}
