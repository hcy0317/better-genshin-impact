using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactGridParamsTests
{
    [Theory]
    [InlineData(1920, 1080, 106, 162, 1171, 918)]
    [InlineData(3840, 2160, 212, 324, 2342, 1836)]
    public void ArtifactsForCapture_MapsTheLogicalGridAcrossResolutions(
        int width,
        int height,
        int x,
        int y,
        int roiWidth,
        int roiHeight)
    {
        var parameters = GridParams.ArtifactsForCapture(new Size(width, height), 1125);

        Assert.Equal(new Rect(x, y, roiWidth, roiHeight), parameters.Roi);
        Assert.True(parameters.FastScroll);
        Assert.Equal(60, parameters.PreScrollDelayMilliseconds);
        Assert.Equal(1125, parameters.TotalItems);
        Assert.Equal(5, parameters.VisibleRows);
        Assert.Equal(5, parameters.FastScrollRows);
        Assert.Equal(new Size(width, height), parameters.CaptureSize);
    }

    [Fact]
    public void ArtifactPagingFallback_UsesDynamicGridDetectionOnlyWhenRequested()
    {
        var parameters = GridParams.ArtifactsForCapture(
            new Size(3840, 2160), 1132, fastScroll: false);

        Assert.False(parameters.FastScroll);
    }

    [Theory]
    [InlineData(1920, 1080, 20, 56, 681, 917)]
    [InlineData(3840, 2160, 40, 112, 1362, 1834)]
    public void CharacterDevelopmentForCapture_ScalesTheCompleteFiveColumnGrid(
        int width,
        int height,
        int x,
        int y,
        int roiWidth,
        int roiHeight)
    {
        var parameters = GridParams.CharacterDevelopmentForCapture(new Size(width, height));

        Assert.Equal(new Rect(x, y, roiWidth, roiHeight), parameters.Roi);
        Assert.Equal(5, parameters.Columns);
        Assert.False(parameters.FastScroll);
        Assert.Equal(50, parameters.S2Round);
    }
}
