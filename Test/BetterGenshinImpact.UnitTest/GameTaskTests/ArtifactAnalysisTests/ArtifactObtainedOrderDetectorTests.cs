using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactObtainedOrderDetectorTests
{
    [Fact]
    public void WhiteRightKnobMeansObtainedOrderIsEnabled()
    {
        using var capture = new Mat(900, 1600, MatType.CV_8UC3, new Scalar(60, 70, 100));
        Cv2.Circle(capture, new Point(1044, 116), 10, Scalar.White, -1);

        Assert.True(ArtifactObtainedOrderDetector.IsEnabled(capture));
    }

    [Fact]
    public void WhiteLeftKnobMeansObtainedOrderIsDisabled()
    {
        using var capture = new Mat(900, 1600, MatType.CV_8UC3, new Scalar(60, 70, 100));
        Cv2.Circle(capture, new Point(1008, 116), 10, Scalar.White, -1);

        Assert.False(ArtifactObtainedOrderDetector.IsEnabled(capture));
    }

    [Theory]
    [InlineData(1600, 900, 1030, 115)]
    [InlineData(3840, 2160, 2472, 276)]
    public void ToggleCenterScalesFromTheObservedSixteenByNineLayout(
        int width,
        int height,
        int expectedX,
        int expectedY)
    {
        Assert.Equal(
            new Point(expectedX, expectedY),
            ArtifactObtainedOrderDetector.ToggleCenter(new Size(width, height)));
    }

    [Fact]
    public void EnabledOrderIsResetByTurningItOffThenOn()
    {
        Assert.Equal(
            [false, true],
            ArtifactObtainedOrderDetector.RequiredStates(initiallyEnabled: true));
    }

    [Fact]
    public void DisabledOrderOnlyNeedsToBeTurnedOn()
    {
        Assert.Equal(
            [true],
            ArtifactObtainedOrderDetector.RequiredStates(initiallyEnabled: false));
    }
}
