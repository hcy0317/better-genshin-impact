using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedFourKeySaurianTests
{
    [UiRecoveryFact("evidence-1417.png")]
    public void FourKeyTransformationKeepsItsKnownExitAndActualSpaceGlyph()
    {
        var path = UiRecoveryFixtures.PathFor("evidence-1417.png");
        Assert.Equal("2C97F1B971D7B51FE632876FCF332138078B4B47A4FDE0E09B2E2AE73C7883AD",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        using var frame = new ImageRegion(Cv2.ImRead(path), 0, 0);
        Assert.True(SaurianUiReader.IsKnownTransformation(frame), SaurianUiReader.Describe(frame));
        foreach (var rect in new[] { new Rect(1785, 965, 65, 60), new Rect(1676, 955, 80, 75),
                     new Rect(805, 1005, 310, 15), new Rect(0, 0, 100, 1080) })
        {
            using var missing = new ImageRegion(frame.SrcMat.Clone(), 0, 0);
            using var area = new Mat(missing.SrcMat, rect);
            area.SetTo(Scalar.Black);
            Assert.False(SaurianUiReader.IsKnownTransformation(missing), "missing=" + rect);
        }
    }
}
