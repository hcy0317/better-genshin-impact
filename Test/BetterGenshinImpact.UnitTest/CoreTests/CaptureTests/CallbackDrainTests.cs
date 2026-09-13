using Fischless.GameCapture.Graphics;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class CallbackDrainTests
{
    [Fact]
    public async Task StopReturnsAnAwaitableDrainWithoutWaitingForItsOwnCallback()
    {
        var lifetime = new FrameCallbackLifetime();
        Assert.True(lifetime.TryEnter());
        Assert.True(lifetime.IsCurrentCallback);
        lifetime.BeginStop();
        var drained = lifetime.WaitForCallbacksAsync();
        Assert.False(drained.IsCompleted);
        Assert.False(lifetime.TryEnter());
        lifetime.Exit();
        await drained.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(lifetime.IsCurrentCallback);
    }
}
