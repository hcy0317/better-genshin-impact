using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class TeleportArrivalProgressTests
{
    [Fact]
    public void LoadingFollowedByThreeFreshWorldFramesConfirmsArrival()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var progress = new TeleportArrivalProgress(clock);
        progress.ConfirmMapClosure(source.Next());
        bool Observe(WorldFrameKind state)
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            return progress.Observe(source.Next(), state);
        }
        Assert.False(Observe(WorldFrameKind.Loading));
        Assert.False(Observe(WorldFrameKind.Loading));
        Assert.False(Observe(WorldFrameKind.Playable));
        Assert.False(Observe(WorldFrameKind.Playable));
        Assert.True(Observe(WorldFrameKind.Playable));
    }

    [Fact]
    public void ClosingMapToWorldOrUnknownPageIsNotTeleportArrival()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var progress = new TeleportArrivalProgress(clock);
        foreach (var state in new[] { WorldFrameKind.Unknown, WorldFrameKind.Playable, WorldFrameKind.Playable, WorldFrameKind.Playable })
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(progress.Observe(source.Next(), state));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConfirmedTeleportCanArriveWhenLoadingWasMissedOrOnlyOneFrameWasSampled(bool singleLoading)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var progress = new TeleportArrivalProgress(clock);
        progress.ConfirmMapClosure(source.Next());
        if (singleLoading)
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            Assert.False(progress.Observe(source.Next(), WorldFrameKind.Loading));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.False(progress.Observe(source.Next(), WorldFrameKind.Playable));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.False(progress.Observe(source.Next(), WorldFrameKind.Playable));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.True(progress.Observe(source.Next(), WorldFrameKind.Playable));
        Assert.False(progress.LoadingObserved);
    }

    [Fact]
    public void DuplicateStaleAndDifferentSessionFramesCannotCompleteConfirmedTeleport()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var progress = new TeleportArrivalProgress(clock);
        progress.ConfirmMapClosure(source.Next());
        clock.Advance(TimeSpan.FromSeconds(1));
        var first = source.Next();
        Assert.False(progress.Observe(first, WorldFrameKind.Playable));
        Assert.False(progress.Observe(first, WorldFrameKind.Playable));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(progress.Observe(first, WorldFrameKind.Playable));
        source.Restart();
        for (var i = 0; i < 6; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            Assert.False(progress.Observe(source.Next(), WorldFrameKind.Playable));
        }
    }

    [Fact]
    public void BurstFramesAndUnknownOverlaysDoNotCountAsStableWorld()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var progress = new TeleportArrivalProgress(clock);
        progress.ConfirmMapClosure(source.Next());
        clock.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 3; i++) Assert.False(progress.Observe(source.Next(), WorldFrameKind.Playable));
        Assert.False(progress.Observe(source.Next(), WorldFrameKind.Unknown));
        clock.Advance(TimeSpan.FromMilliseconds(300));
        Assert.False(progress.Observe(source.Next(), WorldFrameKind.Playable));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.False(progress.Observe(source.Next(), WorldFrameKind.Playable));
        clock.Advance(TimeSpan.FromMilliseconds(150));
        Assert.True(progress.Observe(source.Next(), WorldFrameKind.Playable));
    }
}
