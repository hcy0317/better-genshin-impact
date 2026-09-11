using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DailyRewardsCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void UnclaimedOrUnknownRewardsCannotCompleteTheDailyTask(bool? claimed)
    {
        var error = Assert.Throws<InvalidOperationException>(() => CheckRewardsTask.EnsureCommissionRewardsClaimed(claimed));
        Assert.Contains("每日委托奖励", error.Message);
    }

    [Fact]
    public void ConfirmedClaimCompletesWithoutRequiringASummaryScreenshot()
    {
        CheckRewardsTask.EnsureCommissionRewardsClaimed(true);
    }
}
