using Fischless.GameCapture.Graphics;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class FrameCallbackLifetimeTests
{
    [Fact]
    public async Task StopCoordinator_ShouldDetachAndStopProducerBeforeDrainingCallbacks()
    {
        var lifetime = new FrameCallbackLifetime();
        Assert.True(lifetime.TryEnter());
        var events = new List<string>();

        var stopTask = Task.Run(() => CaptureShutdownCoordinator.Stop(
            lifetime,
            [
                ("detach handlers", () => events.Add("detach handlers")),
                ("stop producer", () => events.Add("stop producer"))
            ],
            [
                ("frame pool", () => events.Add("release frame pool")),
                ("device", () => events.Add("release device"))
            ]));

        Assert.True(SpinWait.SpinUntil(
            () => lifetime.IsStopping && events.Count >= 2,
            TimeSpan.FromSeconds(1)));
        Assert.Equal(["detach handlers", "stop producer"], events);
        Assert.False(stopTask.IsCompleted);

        lifetime.Exit();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.Equal(
            ["detach handlers", "stop producer", "release frame pool", "release device"],
            events);
    }

    [Fact]
    public void StopCoordinator_ShouldAttemptEveryCleanupStepBeforeReportingFailures()
    {
        var lifetime = new FrameCallbackLifetime();
        var completed = new List<string>();
        var detachFailure = new InvalidOperationException("detach failed");
        var releaseFailure = new InvalidOperationException("frame pool release failed");

        var exception = Assert.Throws<AggregateException>(() =>
            CaptureShutdownCoordinator.Stop(
                lifetime,
                [
                    ("detach handlers", () => throw detachFailure),
                    ("stop producer", () => completed.Add("stop producer"))
                ],
                [
                    ("frame pool", () => throw releaseFailure),
                    ("device", () => completed.Add("release device"))
                ]));

        Assert.Equal(["stop producer", "release device"], completed);
        Assert.Equal(new Exception[] { detachFailure, releaseFailure }, exception.InnerExceptions);
    }

    [Fact]
    public async Task BeginStopAndWait_ShouldRejectNewCallbacksAndDrainActiveCallback()
    {
        var lifetime = new FrameCallbackLifetime();
        Assert.True(lifetime.TryEnter());

        var stopTask = Task.Run(lifetime.BeginStopAndWait);
        Assert.True(SpinWait.SpinUntil(() => lifetime.IsStopping, TimeSpan.FromSeconds(1)));

        Assert.False(lifetime.TryEnter());
        Assert.False(stopTask.IsCompleted);

        lifetime.Exit();
        await stopTask.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Reset_ShouldAllowCallbacksAfterPreviousCaptureStopped()
    {
        var lifetime = new FrameCallbackLifetime();
        lifetime.BeginStopAndWait();

        Assert.False(lifetime.TryEnter());

        lifetime.Reset();
        Assert.True(lifetime.TryEnter());
        lifetime.Exit();
    }
}
