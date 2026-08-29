using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactDetailLockDetectorTests
{
    [Fact]
    public void ButtonCenterMatchesTheArtifactDetailLockButton()
    {
        Assert.Equal(
            new Point(1415, 357),
            ArtifactDetailLockDetector.ButtonCenter(new Size(1600, 900)));
    }

    [Theory]
    [InlineData(1600, 900, 1408, 360, 4)]
    [InlineData(3840, 2160, 3379, 864, 10)]
    public void CoralLockInsideDetailButtonMeansLocked(
        int width,
        int height,
        int left,
        int top,
        int size)
    {
        using var capture = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        using var lockIcon = capture.SubMat(new Rect(left, top, size, size));
        lockIcon.SetTo(new Scalar(113, 134, 247));

        Assert.True(ArtifactDetailLockDetector.IsLocked(capture));
    }

    [Fact]
    public void GrayLockInsideDetailButtonMeansUnlocked()
    {
        using var capture = new Mat(900, 1600, MatType.CV_8UC3, Scalar.White);
        using var lockIcon = capture.SubMat(new Rect(1408, 360, 8, 8));
        lockIcon.SetTo(new Scalar(160, 160, 160));

        Assert.False(ArtifactDetailLockDetector.IsLocked(capture));
    }
}
