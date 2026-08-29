using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactUiCoordinateMapperTests
{
    [Theory]
    [InlineData(1280, 720, 824, 92)]
    [InlineData(1366, 768, 879, 98)]
    [InlineData(1600, 900, 1030, 115)]
    [InlineData(1920, 1080, 1236, 138)]
    [InlineData(2560, 1440, 1648, 184)]
    [InlineData(3840, 2160, 2472, 276)]
    public void LogicalPointMapsAcrossSixteenByNineCaptures(
        int width, int height, int expectedX, int expectedY)
    {
        Assert.Equal(
            new Point(expectedX, expectedY),
            ArtifactUiCoordinateMapper.ToCapturePoint(
                new Size(width, height), 1030, 115));
    }

    [Fact]
    public void LogicalRegionNormalizesBackToItsReferenceSize()
    {
        using var capture = new Mat(2160, 3840, MatType.CV_8UC3, new Scalar(10, 20, 30));
        using var normalized = ArtifactUiCoordinateMapper.CropNormalized(
            capture, 990, 92, 75, 48);

        Assert.Equal(new Size(75, 48), normalized.Size());
    }
}
