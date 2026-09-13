using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class LatestOwnedWorkTests
{
    [Fact]
    public async Task StopDiscardsQueuedFramesButWaitsForTheInFlightNativeOwner()
    {
        using var started = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        var frames = Enumerable.Range(0, 4).Select(_ => new Frame()).ToArray();
        var queue = new LatestOwnedWork<Frame>(_ => { started.Set(); finish.Wait(TimeSpan.FromSeconds(5)); }, _ => { });
        queue.Enqueue(frames[0]);
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        queue.Enqueue(frames[1]);
        queue.Enqueue(frames[2]);
        Assert.Equal(1, frames[1].Disposals);
        var stopped = queue.DisposeAsync().AsTask();
        Assert.False(stopped.IsCompleted);
        Assert.Equal(0, frames[0].Disposals);
        queue.Enqueue(frames[3]);
        Assert.Equal(1, frames[2].Disposals);
        Assert.Equal(1, frames[3].Disposals);
        finish.Set();
        await stopped;
        await queue.DisposeAsync();
        Assert.All(frames, frame => Assert.Equal(1, frame.Disposals));
    }

    private sealed class Frame : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
}
