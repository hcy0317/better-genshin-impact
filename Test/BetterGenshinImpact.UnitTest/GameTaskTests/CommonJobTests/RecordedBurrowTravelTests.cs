using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class RecordedBurrowTravelTests(ITestOutputHelper output)
{
    [UiRecoveryTheory("burrow-travel-20261006.png")]
    [InlineData("burrow-travel-20261006.png")]
    public void TravelLayoutRequiresItsAbilityExitAndUnobscuredWorld(string file)
    {
        using var frame = new ImageRegion(Cv2.ImRead(UiRecoveryFixtures.PathFor(file)), 0, 0);
        if (Environment.GetEnvironmentVariable("BGI_EMIT_BURROW_TRAVEL") == "1")
        {
            var scale = frame.Width / 1920d;
            using var area = new Mat(frame.SrcMat, new Rect((int)Math.Round(1803 * scale), (int)Math.Round(974 * scale),
                (int)Math.Round(33 * scale), (int)Math.Round(43 * scale)));
            using var resized = new Mat();
            Cv2.Resize(area, resized, new Size(33, 43));
            using var gray = new Mat();
            Cv2.CvtColor(resized, gray, ColorConversionCodes.BGR2GRAY);
            output.WriteLine("BURROW_TRAVEL=" + Convert.ToBase64String(gray.ToBytes()));
        }
        Assert.True(SaurianUiReader.IsKnownTransformation(frame), SaurianUiReader.Describe(frame));
        foreach (var rect in new[] { new Rect(1570, 955, 85, 72), new Rect(1785, 965, 65, 60),
            new Rect(805, 1005, 310, 15), new Rect(0, 0, 100, 1080) })
        {
            using var missing = new ImageRegion(frame.SrcMat.Clone(), 0, 0);
            var scale = frame.Width / 1920d;
            using var area = new Mat(missing.SrcMat, new Rect((int)(rect.X * scale), (int)(rect.Y * scale), (int)(rect.Width * scale), (int)(rect.Height * scale)));
            area.SetTo(Scalar.Black);
            Assert.False(SaurianUiReader.IsKnownTransformation(missing), rect.ToString());
        }
        using var dim = new Mat();
        frame.SrcMat.ConvertTo(dim, frame.SrcMat.Type(), .7);
        using var dimFrame = new ImageRegion(dim.Clone(), 0, 0);
        Assert.False(SaurianUiReader.IsKnownTransformation(dimFrame));
    }
}
