using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactYasScrollTests
{
    [Fact]
    public void RowDetector_CompletesOnlyAfterTheFlagChangesAndReturns()
    {
        var detector = new ArtifactRowScrollDetector(new Vec3b(20, 30, 40));

        Assert.False(detector.Observe(new Vec3b(20, 30, 40)));
        Assert.False(detector.Observe(new Vec3b(80, 90, 100)));
        Assert.True(detector.Observe(new Vec3b(21, 30, 40)));
    }

    [Fact]
    public void GridLayout_UsesTheYasFiveByEightArtifactPositions()
    {
        var roi = GridParams.ArtifactRoiForCapture(new Size(1920, 1080));
        var cells = ArtifactGridLayout.CellsInRoi(new Size(1920, 1080), roi);

        Assert.Equal(40, cells.Count);
        Assert.Equal(new Rect(13, 17, 122, 151), cells[0].Rect);
        Assert.Equal(4, cells[^1].RowNum);
        Assert.Equal(7, cells[^1].ColNum);
    }

    [Theory]
    [InlineData(141, 5, 0, 5)]
    [InlineData(141, 5, 135, 1)]
    [InlineData(141, 5, 136, 0)]
    public void RowsToScroll_MatchesTheRemainingInventoryRows(
        int totalRows,
        int visibleRows,
        int scrolledRows,
        int expected)
    {
        Assert.Equal(expected, ArtifactRowScrollPlanner.RowsToScroll(
            totalRows, visibleRows, scrolledRows));
    }

    [Theory]
    [InlineData(8.0, 5, 38)]
    [InlineData(8.4, 5, 40)]
    [InlineData(1.0, 1, 0)]
    public void EstimatedScrollInputs_MatchesYasBurstCalculation(
        double averageInputsPerRow,
        int rows,
        int expected)
    {
        Assert.Equal(expected, ArtifactRowScrollPlanner.EstimateInputCount(
            averageInputsPerRow, rows));
    }

}
