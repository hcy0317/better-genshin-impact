using BetterGenshinImpact.GameTask.AutoBoss;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoBossTests;

public class RewardNavigationProgressTests
{
    [Fact]
    public void IsTimedOutUsesConfiguredBoundary()
    {
        var progress = new RewardNavigationProgress(
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(3));

        Assert.False(progress.IsTimedOut(TimeSpan.FromSeconds(19.9)));
        Assert.True(progress.IsTimedOut(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void MissingHeartbeatIsRateLimited()
    {
        var progress = new RewardNavigationProgress(
            TimeSpan.FromSeconds(20),
            TimeSpan.FromSeconds(3));

        Assert.False(progress.ShouldLogMissing(TimeSpan.FromSeconds(2.9)));
        Assert.True(progress.ShouldLogMissing(TimeSpan.FromSeconds(3)));
        Assert.False(progress.ShouldLogMissing(TimeSpan.FromSeconds(5.9)));
        Assert.True(progress.ShouldLogMissing(TimeSpan.FromSeconds(6)));
    }
}
