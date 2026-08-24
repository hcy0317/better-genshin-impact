using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class SereniteaPotExitPolicyTests
{
    [Theory]
    [InlineData(TalkOptionRes.NotFound, false, true)]
    [InlineData(TalkOptionRes.FoundButNotOrange, false, true)]
    [InlineData(TalkOptionRes.FoundAndClick, false, false)]
    [InlineData(TalkOptionRes.NotFound, true, false)]
    public void ShouldRecoverMainUi_ReturnsExpectedResult(
        TalkOptionRes quitOption,
        bool isMainUi,
        bool expected)
    {
        Assert.Equal(expected, SereniteaPotExitPolicy.ShouldRecoverMainUi(quitOption, isMainUi));
    }

    [Fact]
    public void EnsureRecovered_ThrowsWhenMainUiStillCannotBeReached()
    {
        Assert.Throws<InvalidOperationException>(() => SereniteaPotExitPolicy.EnsureRecovered(isMainUi: false));
    }
}
