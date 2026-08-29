using BetterGenshinImpact.GameTask.AutoDomain;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomainTests;

public class PetrifiedTreeSearchProgressTests
{
    [Theory]
    [InlineData(0, 90)]
    [InlineData(10, 30)]
    [InlineData(120, 120)]
    [InlineData(600, 300)]
    public void NormalizeTimeoutSeconds_ShouldKeepTreeSearchWithinSafeBounds(int configured, int expected)
    {
        Assert.Equal(expected, PetrifiedTreeSearchProgress.NormalizeTimeoutSeconds(configured));
    }

    [Fact]
    public void Observe_ShouldReportEveryFifteenSecondsAndTimeoutAtConfiguredLimit()
    {
        var progress = new PetrifiedTreeSearchProgress(timeoutSeconds: 90);

        Assert.False(progress.Observe(TimeSpan.FromSeconds(14)).ShouldLog);
        Assert.True(progress.Observe(TimeSpan.FromSeconds(15)).ShouldLog);
        Assert.False(progress.Observe(TimeSpan.FromSeconds(29)).ShouldLog);
        Assert.True(progress.Observe(TimeSpan.FromSeconds(30)).ShouldLog);
        Assert.False(progress.Observe(TimeSpan.FromSeconds(89)).TimedOut);
        Assert.True(progress.Observe(TimeSpan.FromSeconds(90)).TimedOut);
    }
}
