using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedS63FlightTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public void RecordedS74TransformationRetainsItsThreeIndependentHudSignals()
    {
        var path = Environment.GetEnvironmentVariable("BGI_S74_TRANSFORMATION_FILE");
        Assert.True(File.Exists(path));
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0);
        Assert.True(SaurianUiReader.IsKnownTransformation(frame));
        foreach (var rect in new[] { new Rect(805, 1005, 310, 15), new Rect(1799, 970, 41, 51),
            new Rect(1682, 959, 65, 66), new Rect(0, 0, 150, 1080) })
        {
            using var missing = new ImageRegion(frame.SrcMat.Clone(), 0, 0);
            using var area = new Mat(missing.SrcMat, rect);
            area.SetTo(Scalar.Black);
            Assert.False(SaurianUiReader.IsKnownTransformation(missing), $"missing {rect}");
        }
        using var dim = new Mat();
        frame.SrcMat.ConvertTo(dim, frame.SrcMat.Type(), .3);
        using var dimFrame = new ImageRegion(dim.Clone(), 0, 0);
        Assert.False(SaurianUiReader.IsKnownTransformation(dimFrame));
        foreach (var file in new[] { "inactive-zhongli-20260911.png", "s32-map-marker-20260916.png", "s32-food-revive-20260916.png" })
        {
            using var ordinary = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", file)), 0, 0);
            Assert.False(SaurianUiReader.IsKnownTransformation(ordinary), file);
        }
        if (Environment.GetEnvironmentVariable("BGI_S74_CORPUS_SCAN") == "1")
        {
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path!)!, "error-2026092*.png")
                .OrderDescending().Take(128))
            {
                using var candidate = new ImageRegion(Cv2.ImRead(file), 0, 0);
                if (SaurianUiReader.IsKnownTransformation(candidate)) output.WriteLine("known-form: " + file);
            }
        }
    }

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
