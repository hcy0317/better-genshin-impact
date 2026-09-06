using BetterGenshinImpact.GameTask.AutoFight;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class NativeCombatTaskGroupTests
{
    [Fact]
    public async Task PreCancelledParent_ShouldNotInvokeGameOrDetector()
    {
        using var parent = new CancellationTokenSource();
        parent.Cancel();
        using var session = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeCombatTaskGroup.RunAsync(
            session, parent.Token,
            () => { Interlocked.Increment(ref calls); return Task.CompletedTask; },
            () => { Interlocked.Increment(ref calls); return Task.CompletedTask; }));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetectorCompletionOrFailure_ShouldAwaitCancelledCombat(bool detectorFails)
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var session = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var run = NativeCombatTaskGroup.RunAsync(session, parent.Token, async () =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, session.Token); }
            finally { stopped = true; }
        }, async () =>
        {
            await started.Task.WaitAsync(session.Token);
            if (detectorFails) throw new InvalidOperationException("detector failed");
        });

        if (detectorFails) await Assert.ThrowsAsync<InvalidOperationException>(() => run);
        else await run;
        Assert.True(stopped);
        Assert.False(parent.IsCancellationRequested);
    }

    [Fact]
    public async Task ParentCancellation_ShouldAwaitChildrenAndRemainCancellation()
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var session = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var inputs = 0;
        var run = NativeCombatTaskGroup.RunAsync(session, parent.Token, async () =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, session.Token);
                inputs++;
            }
            finally { stopped = true; }
        }, async () => { await started.Task.WaitAsync(session.Token); await parent.CancelAsync(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.True(stopped);
        Assert.Equal(0, inputs);
    }

    [Fact]
    public async Task CompletedOptionalAssistant_ShouldNotEndCombat()
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var session = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var assisted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var combatReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await NativeCombatTaskGroup.RunAsync(session, parent.Token, async () =>
        {
            await assisted.Task.WaitAsync(session.Token);
            Assert.False(session.IsCancellationRequested);
            combatReady.SetResult();
            await Task.Delay(Timeout.Infinite, session.Token);
        }, () => combatReady.Task.WaitAsync(session.Token), () => { assisted.SetResult(); return Task.CompletedTask; });
    }

    [Fact]
    public async Task AssistantFailure_ShouldCancelAndAwaitBothRequiredChildren()
    {
        using var parent = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var session = CancellationTokenSource.CreateLinkedTokenSource(parent.Token);
        var combatStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detectorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = 0;
        async Task RunChild(TaskCompletionSource started)
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, session.Token); }
            finally { Interlocked.Increment(ref stopped); }
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeCombatTaskGroup.RunAsync(
            session, parent.Token, () => RunChild(combatStarted), () => RunChild(detectorStarted), async () =>
            {
                await Task.WhenAll(combatStarted.Task, detectorStarted.Task).WaitAsync(session.Token);
                throw new InvalidOperationException("assistant failed");
            }));
        Assert.Equal(2, stopped);
    }
}
