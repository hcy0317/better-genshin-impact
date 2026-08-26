using BetterGenshinImpact.GameTask.AutoPathing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathProgressHeartbeatTests
{
    [Fact]
    public void ShouldReport_ShouldWaitForIntervalAndThrottleFollowingReports()
    {
        var startedAt = new DateTime(2026, 8, 24, 20, 58, 38, DateTimeKind.Utc);
        var heartbeat = new PathProgressHeartbeat(startedAt, TimeSpan.FromSeconds(15));

        Assert.False(heartbeat.ShouldReport(startedAt.AddSeconds(14.999)));
        Assert.True(heartbeat.ShouldReport(startedAt.AddSeconds(15)));
        Assert.False(heartbeat.ShouldReport(startedAt.AddSeconds(29.999)));
        Assert.True(heartbeat.ShouldReport(startedAt.AddSeconds(30)));
    }

    [Fact]
    public void Constructor_ShouldRejectNonPositiveInterval()
    {
        var startedAt = new DateTime(2026, 8, 24, 20, 58, 38, DateTimeKind.Utc);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PathProgressHeartbeat(startedAt, TimeSpan.Zero));
    }

    [Fact]
    public void ClimbWatchdog_ShouldBoundOnlyClimbMovementAtConfiguredTimeout()
    {
        var startedAt = new DateTime(2026, 8, 24, 20, 58, 38, DateTimeKind.Utc);
        var watchdog = new PathMovementWatchdog(startedAt, TimeSpan.FromSeconds(60));

        Assert.False(watchdog.ShouldAbort(isClimbing: true, startedAt.AddSeconds(59.999)));
        Assert.True(watchdog.ShouldAbort(isClimbing: true, startedAt.AddSeconds(60)));
        Assert.False(watchdog.ShouldAbort(isClimbing: false, startedAt.AddMinutes(4)));
    }
}
