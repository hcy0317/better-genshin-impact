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

    [Fact]
    public void OriginalResinSearchCoversTheCurrentRightTopUiRegion()
    {
        Assert.Equal(new Rect(1200, 25, 580, 50), ResinStatus.GetOriginalResinSearchRect(1d));
    }
}
