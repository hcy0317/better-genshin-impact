using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedSurfboardSceneTests
{
    // Original run 0b3cca80-3ae6-4787-a414-e5a9a5223e7c, source 282123.
    // The account-bearing source is supplied through BGI_UI_RECOVERY_FIXTURE_ROOT, outside public source control.
    [UiRecoveryTheory("evidence-3991.png", "evidence-4008.png")]
    [InlineData("evidence-3991.png")]
    [InlineData("evidence-4008.png")]
    public void RecordedSurfboardControlsRemainPlayableWithoutAcceptingAnIncompleteHud(string file)
    {
        using var frame = new ImageRegion(Cv2.ImRead(UiRecoveryFixtures.PathFor(file)), 0, 0);
        Assert.False(frame.SrcMat.Empty());
        Assert.True(SaurianUiReader.IsKnownTransformation(frame), SaurianUiReader.Describe(frame));
        Assert.Equal(WorldFrameKind.Playable, WorldFrameAvailability.ReadWorld(frame,
            SaurianUiReader.IsKnownTransformation, _ => false, _ => false, new NoOcr()));
        foreach (var bounds in new[] { new Rect(1785, 965, 65, 60), new Rect(1582, 959, 66, 70),
                     new Rect(805, 1005, 310, 15), new Rect(0, 0, 100, 1080) })
        {
            using var missing = new ImageRegion(frame.SrcMat.Clone(), 0, 0);
            using var area = new Mat(missing.SrcMat, bounds);
            area.SetTo(Scalar.Black);
            Assert.False(SaurianUiReader.IsKnownTransformation(missing), "missing=" + bounds);
        }
        using var darkPixels = new Mat();
        frame.SrcMat.ConvertTo(darkPixels, frame.SrcMat.Type(), .7);
        using var dark = new ImageRegion(darkPixels.Clone(), 0, 0);
        Assert.False(SaurianUiReader.IsKnownTransformation(dark));
    }

    private sealed class NoOcr : IOcrService
    {
        public string OcrWithoutDetector(Mat mat) => throw new InvalidOperationException("Unobscured surfboard controls do not need OCR.");
        public string Ocr(Mat mat) => throw new NotSupportedException();
        public OcrResult OcrResult(Mat mat) => throw new NotSupportedException();
    }
}
