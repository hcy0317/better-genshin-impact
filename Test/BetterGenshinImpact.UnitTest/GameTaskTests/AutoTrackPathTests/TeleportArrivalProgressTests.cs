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
}
