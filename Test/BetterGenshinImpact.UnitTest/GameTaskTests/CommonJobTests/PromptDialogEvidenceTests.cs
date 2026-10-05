using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class PromptDialogEvidenceTests
{
    [UiRecoveryFact("party-background-star-20261005.png")]
    public void RecordedPartyBackgroundSparkleIsNotAConfirmationDialog()
    {
        using var pixels = Cv2.ImRead(UiRecoveryFixtures.PathFor("party-background-star-20261005.png"));
        using var frame = new ImageRegion(pixels.Clone(), 0, 0);
        using var oldMatch = frame.Find(ElementRecognition.Get("PromptDialogLeftBottomStar", frame));
        Assert.True(oldMatch.IsExist());
        Assert.True(Bv.IsInPartyViewUi(frame));
        Assert.False(Bv.IsInPromptDialog(frame));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    public void RecordedFoodDialogStillHasIndependentPanelCorners(int width, int height)
    {
        using var pixels = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "s32-food-revive-20260916.png"));
        using var scaled = new Mat();
        Cv2.Resize(pixels, scaled, new Size(width, height));
        using var frame = new ImageRegion(scaled.Clone(), 0, 0);
        Assert.True(Bv.IsInPromptDialog(frame));
    }
}
