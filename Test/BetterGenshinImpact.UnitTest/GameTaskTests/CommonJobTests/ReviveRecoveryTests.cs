using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ReviveRecoveryTests
{
    [Theory]
    [InlineData("复苏", "复苏", true)]
    [InlineData("复 苏", "复苏", true)]
    [InlineData("Revive", "Revive", true)]
    [InlineData("确认退出", "复苏", false)]
    [InlineData("", "复苏", false)]
    public void IsReviveText_ShouldUseLocalizedTextAndIgnoreOcrWhitespace(
        string text,
        string localizedRevive,
        bool expected)
    {
        Assert.Equal(expected, Bv.IsReviveText(text, localizedRevive));
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void IsReviveRecoveryConfirmed_ShouldRequireClickAndMainUi(
        bool clicked,
        bool returnedToMainUi,
        bool expected)
    {
        Assert.Equal(expected, Bv.IsReviveRecoveryConfirmed(clicked, returnedToMainUi));
    }
}
