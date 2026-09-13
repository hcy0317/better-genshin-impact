using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class DispatcherDrainControllerTests
{
    [Fact]
    public async Task ReentrantStopSharesThePublishedDrainInsteadOfReleasingTwice()
    {
        DispatcherDrainController? controller = null;
        Task? nested = null;
        var releases = 0;
        controller = new(() => { }, () => { if (++releases == 1) nested = controller!.StopAsync(); });
        controller.Start();
        var stopped = controller.StopAsync();
        await stopped;
        Assert.Same(stopped, nested);
        Assert.Equal(1, releases);
    }

    [Fact]
    public async Task FailedReleaseCanBeRetriedWithoutReopeningCallbackAdmission()
    {
        var attempts = 0;
        var controller = new DispatcherDrainController(() => { }, () =>
        {
            if (++attempts == 1) throw new InvalidOperationException("capture busy");
        });
        controller.Start();
        await Assert.ThrowsAsync<AggregateException>(controller.StopAsync);
        Assert.False(controller.TryEnter());
        await controller.StopAsync();
        Assert.Equal(2, attempts);
        controller.Start();
        Assert.True(controller.TryEnter());
        controller.Exit();
        await controller.StopAsync();
    }

    [Fact]
    public async Task StartupFailureCannotAdmitTicksAndStillReleasesPartialCapture()
    {
        var releases = 0;
        var controller = new DispatcherDrainController(() => { }, () => releases++);
        controller.PrepareStart();
        Assert.False(controller.TryEnter());
        await controller.StopAsync();
        Assert.Equal(1, releases);
        Assert.Throws<InvalidOperationException>(controller.Activate);
        controller.Start();
        Assert.True(controller.TryEnter());
        controller.Exit();
        await controller.StopAsync();
    }

    [Fact]
    public async Task StopErrorsAreReportedAfterCallbackDrain()
    {
        var released = false;
        var controller = new DispatcherDrainController(() => throw new InvalidOperationException("timer"), () => released = true);
        controller.Start();
        controller.TryEnter();
        var stopped = controller.StopAsync();
        Assert.False(released);
        controller.Exit();
        await Assert.ThrowsAsync<AggregateException>(() => stopped);
        Assert.True(released);
        Assert.Throws<InvalidOperationException>(controller.Start);
    }

    [Fact]
    public async Task TimerIsQuiescedBeforeDrainAndResourcesAreReleasedOnlyAfterTheLastCallback()
    {
        var events = new List<string>();
        var controller = new DispatcherDrainController(() => events.Add("timer-stop"), () => events.Add("capture-release"));
        controller.Start();
        Assert.True(controller.TryEnter());
        var stopped = controller.StopAsync();
        Assert.Equal(new[] { "timer-stop" }, events);
        Assert.False(stopped.IsCompleted);
        Assert.False(controller.TryEnter());
        controller.Exit();
        await stopped;
        await controller.StopAsync();
        Assert.Equal(new[] { "timer-stop", "capture-release" }, events);
        controller.Start();
        Assert.True(controller.TryEnter());
        controller.Exit();
        await controller.StopAsync();
        Assert.Equal(2, events.Count(item => item == "capture-release"));
    }
}
