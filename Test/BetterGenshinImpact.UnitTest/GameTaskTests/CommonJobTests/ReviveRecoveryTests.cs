using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ReviveRecoveryTests
{
    [Theory]
    [InlineData(true, true, false, ReviveUiState.FoodPrompt)]
    [InlineData(true, false, true, ReviveUiState.None)]
    [InlineData(false, false, true, ReviveUiState.FullPartyDefeat)]
    [InlineData(false, false, false, ReviveUiState.None)]
    internal void ConfirmationDialogCannotBeMistakenForFreePartyRevive(
        bool confirm, bool title, bool button, ReviveUiState expected)
    {
        Assert.Equal(expected, Bv.ClassifyReviveEvidence(confirm, title, button));
    }

    [Theory]
    [InlineData("复苏", "复苏", true)]
    [InlineData("复 苏", "复苏", true)]
    [InlineData("Revive", "Revive", true)]
    [InlineData("确认退出", "复苏", false)]
    [InlineData("无法复苏", "复苏", false)]
    [InlineData("复苏道具不足", "复苏", false)]
    [InlineData("Cannot Revive", "Revive", false)]
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
