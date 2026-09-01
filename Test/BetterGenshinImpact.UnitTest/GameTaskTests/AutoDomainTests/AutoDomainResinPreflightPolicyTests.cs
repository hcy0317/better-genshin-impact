using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoDomain.Model;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomainTests;

public class AutoDomainResinPreflightPolicyTests
{
    [Theory]
    [InlineData(6, 0, false)]
    [InlineData(19, 0, false)]
    [InlineData(20, 0, true)]
    [InlineData(0, 1, true)]
    public void RequiresOneClaimableResinSource(int original, int condensed, bool expected)
    {
        Assert.Equal(expected, AutoDomainResinPreflightPolicy.HasClaimableResin(original, condensed));
    }

    [Theory]
    [InlineData(false, 0, 0, true)]
    [InlineData(true, 0, 0, true)]
    [InlineData(true, 1, 0, false)]
    [InlineData(true, 0, 1, false)]
    public void OnlySupplementalResinDefersAvailabilityToTheRewardDialog(
        bool specifyResinUse,
        int transientResinUseCount,
        int fragileResinUseCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutoDomainResinPreflightPolicy.ShouldCheckMapResin(
                specifyResinUse,
                transientResinUseCount,
                fragileResinUseCount));
    }

    [Theory]
    [InlineData(false, 1, 0, true)]
    [InlineData(true, 0, 0, true)]
    [InlineData(true, 1, 0, false)]
    [InlineData(true, 0, 1, false)]
    public void RewardPromptOnlyExitsWhenNoConfiguredSupplementalResinRemains(
        bool specifyResinUse,
        int transientResinRemainCount,
        int fragileResinRemainCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutoDomainResinPreflightPolicy.ShouldExitSupplementPrompt(
                specifyResinUse,
                transientResinRemainCount,
                fragileResinRemainCount));
    }

    [Theory]
    [InlineData(false, "须臾树脂", false)]
    [InlineData(true, "浓缩树脂", false)]
    [InlineData(true, "原粹树脂", false)]
    [InlineData(true, "须臾树脂", true)]
    [InlineData(true, "脆弱树脂", true)]
    public void PreferredSupplementalResinIsPreparedBeforeEnteringDomain(
        bool specifyResinUse,
        string preferredResinName,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutoDomainResinPreflightPolicy.ShouldPrepareSupplementalResinBeforeDomain(
                specifyResinUse,
                preferredResinName));
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(1, 1, false)]
    [InlineData(3, 2, true)]
    [InlineData(3, 3, false)]
    public void SupplementalPreparationNeverExceedsConfiguredDomainRounds(
        int domainRoundNum,
        int preparedCount,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutoDomainResinPreflightPolicy.CanPrepareAnotherSupplementalResin(
                domainRoundNum,
                preparedCount));
    }

    [Fact]
    public void OriginalResinSearchCoversTheCurrentRightTopUiRegion()
    {
        Assert.Equal(new Rect(1200, 25, 580, 50), ResinStatus.GetOriginalResinSearchRect(1d));
    }
}
