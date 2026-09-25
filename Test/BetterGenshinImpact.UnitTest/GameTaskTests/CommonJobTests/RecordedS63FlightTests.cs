using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedS63FlightTests
{
    [OfflineNativeDecisionFact]
    public void PreviousThreeRecordedFormsRemainRecognized()
    {
        var paths = Environment.GetEnvironmentVariable("BGI_S63_PREVIOUS_FORMS")?.Split('|') ?? [];
        Assert.Equal(3, paths.Length);
        foreach (var path in paths)
        {
            using var frame = new ImageRegion(Cv2.ImRead(path), 0, 0);
            Assert.True(SaurianUiReader.IsKnownTransformation(frame), path);
        }
        using var black = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        Assert.False(SaurianUiReader.IsKnownTransformation(black));
    }

    [OfflineNativeDecisionFact]
    public void IndependentRecordedFlightHudIsKnownWithoutTreatingOrdinaryHudAsTransformation()
    {
        var paths = Environment.GetEnvironmentVariable("BGI_S63_FLIGHT_FRAMES")?.Split('|') ?? [];
        Assert.Equal(2, paths.Length);
        foreach (var path in paths)
        {
            using var image = new ImageRegion(Cv2.ImRead(path), 0, 0);
            Assert.True(SaurianUiReader.IsKnownTransformation(image), path);
            foreach (var roi in new[] { new Rect(810, 1007, 280, 7), new Rect(1799, 970, 41, 51), new Rect(1682, 959, 65, 66) })
            {
                using var masked = image.SrcMat.Clone();
                using (var crop = new Mat(masked, roi)) crop.SetTo(Scalar.Black);
                using var missing = new ImageRegion(masked.Clone(), 0, 0);
                Assert.False(SaurianUiReader.IsKnownTransformation(missing), $"missing {roi}: {path}");
            }
            using var dim = new Mat();
            image.SrcMat.ConvertTo(dim, image.SrcMat.Type(), .3);
            using var dimFrame = new ImageRegion(dim.Clone(), 0, 0);
            Assert.False(SaurianUiReader.IsKnownTransformation(dimFrame));
            using var distorted = new Mat();
            Cv2.Resize(image.SrcMat, distorted, new Size(1280, 800));
            using var wrongAspect = new ImageRegion(distorted.Clone(), 0, 0);
            Assert.False(SaurianUiReader.IsKnownTransformation(wrongAspect));
        }
        using var ordinary = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Ui", "inactive-zhongli-20260911.png")), 0, 0);
        Assert.False(SaurianUiReader.IsKnownTransformation(ordinary));
        Assert.Null(System.Windows.Application.Current);
    }
}
