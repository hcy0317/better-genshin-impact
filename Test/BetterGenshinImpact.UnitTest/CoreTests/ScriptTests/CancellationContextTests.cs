using BetterGenshinImpact.Core.Script;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class CancellationContextTests
{
    [Fact]
    public async Task RepeatedStopAndRetirementShareTheInFlightCancellationWithoutBlockingTheCaller()
    {
        var context = new CancellationContext();
        var run = context.EnterRun();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;
        using var registration = context.GetTokenOrNone().Register(() =>
        { Interlocked.Increment(ref callbacks); entered.SetResult(); release.Task.GetAwaiter().GetResult(); });
        try
        {
            var first = context.CancelAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(first, context.CancelAsync());
            context.Cancel(); // 已在途的重复停止不再次同步等待。
            var retire = run.DisposeAsync().AsTask();
            Assert.False(retire.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => context.EnterRun());
            release.SetResult();
            await retire.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, callbacks);
        }
        finally { release.TrySetResult(); await run.DisposeAsync(); }
    }

    [Fact]
    public async Task ChildTasksShareOneRunCancellationGeneration()
    {
        var context = new CancellationContext();
        var run = context.EnterRun();
        try
        {
            var token = context.GetTokenOrNone();
            context.Set();
            context.Clear();
            Assert.Equal(token, context.GetTokenOrNone());
            context.ManualCancel();
            context.Set();
            context.Clear();
            Assert.True(context.GetTokenOrNone().IsCancellationRequested);
            Assert.True(context.IsManualStop);
        }
        finally { await run.DisposeAsync(); }
        Assert.Equal(CancellationToken.None, context.GetTokenOrNone());
    }

    [Fact]
    public void ReplacingAnIdleGenerationRetiresItsOldSource()
    {
        var context = new CancellationContext();
        var old = context.Cts;
        context.Set();
        Assert.Throws<ObjectDisposedException>(() => old.Token);
        Assert.False(context.GetTokenOrNone().IsCancellationRequested);
        context.Clear();
    }

    [Fact]
    public async Task ClearDoesNotDisposeACancellationCallbackThatIsStillRunning()
    {
        var context = new CancellationContext();
        var source = context.Cts;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = source.Token.Register(() =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
        });
        var cancel = Task.Run(context.Cancel);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            context.Clear();
            Assert.Null(Record.Exception(() => _ = source.Token));
            context.Set();
            Assert.False(context.GetTokenOrNone().IsCancellationRequested);
        }
        finally { release.TrySetResult(); await cancel; context.Clear(); }
        Assert.Throws<ObjectDisposedException>(() => source.Token);
    }

    [Fact]
    public void GetTokenOrNoneReturnsNonCanceledFallbackAfterClear()
    {
        var context = CancellationContext.Instance;
        context.Set();
        context.Clear();

        var token = context.GetTokenOrNone();

        Assert.Equal(CancellationToken.None, token);
        Assert.False(token.IsCancellationRequested);
    }
}
