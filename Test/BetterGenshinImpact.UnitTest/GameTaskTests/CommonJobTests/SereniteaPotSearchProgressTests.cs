using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class SereniteaPotSearchProgressTests
{
    [Fact]
    public void FindAYuanApproachMustNotUseAsyncVoidTaskConstructor()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "BetterGenshinImpact",
            "GameTask",
            "Common",
            "Job",
            "GoToSereniteaPotTask.cs"));

        Assert.DoesNotContain("new Task(async", source, StringComparison.Ordinal);
        Assert.Contains("await Delay(50, approachToken);", source, StringComparison.Ordinal);
    }

    [Fact]
    public void IsTimedOut_ShouldBoundTubbySearchToThirtySeconds()
    {
        var progress = new SereniteaPotSearchProgress(
            timeout: TimeSpan.FromSeconds(30),
            heartbeatInterval: TimeSpan.FromSeconds(10));

        Assert.False(progress.IsTimedOut(TimeSpan.FromSeconds(29.9)));
        Assert.True(progress.IsTimedOut(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public void ShouldLogHeartbeat_ShouldReportProgressOncePerInterval()
    {
        var progress = new SereniteaPotSearchProgress(
            timeout: TimeSpan.FromSeconds(30),
            heartbeatInterval: TimeSpan.FromSeconds(10));

        Assert.False(progress.ShouldLogHeartbeat(TimeSpan.FromSeconds(9)));
        Assert.True(progress.ShouldLogHeartbeat(TimeSpan.FromSeconds(10)));
        Assert.False(progress.ShouldLogHeartbeat(TimeSpan.FromSeconds(10.1)));
        Assert.True(progress.ShouldLogHeartbeat(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void TeleportProgressRequiresTwoConsecutiveMapClosedChecks()
    {
        var progress = new SereniteaPotTeleportProgress();

        Assert.False(progress.Observe(isInBigMapUi: false));
        Assert.True(progress.Observe(isInBigMapUi: false));
    }

    [Fact]
    public void TeleportProgressResetsWhenMapReappears()
    {
        var progress = new SereniteaPotTeleportProgress();

        Assert.False(progress.Observe(isInBigMapUi: false));
        Assert.False(progress.Observe(isInBigMapUi: true));
        Assert.False(progress.Observe(isInBigMapUi: false));
        Assert.True(progress.Observe(isInBigMapUi: false));
    }
}
