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
    public void GridLayout_ScrollFlagUsesAPatchMeanInsteadOfOneNoisyPixel()
    {
        using var capture = new Mat(
            new Size(9, 9),
            MatType.CV_8UC3,
            new Scalar(20, 30, 40));
        capture.Set(4, 4, new Vec3b(65, 75, 85));

        var sampled = ArtifactGridLayout.ReadBgr(capture, new Point(4, 4));

        Assert.True(ArtifactRowScrollDetector.IsNear(
            new Vec3b(20, 30, 40), sampled));
    }

    [Fact]
    public void GridAlignment_AcceptsTwoCompleteRowsAtTheYasTemplatePositions()
    {
        var captureSize = new Size(1920, 1080);
        var roi = GridParams.ArtifactRoiForCapture(captureSize);
        var items = ArtifactGridLayout.CellsInRoi(captureSize, roi)
            .Take(16)
            .Select(cell => cell.Rect)
            .ToArray();

        Assert.True(ArtifactGridAlignmentPlanner.IsAligned(
            items, captureSize, roi, 8));
    }

    [Fact]
    public void GridAlignment_RejectsAnOverscrolledPartialTopRow()
    {
        var captureSize = new Size(1920, 1080);
        var roi = GridParams.ArtifactRoiForCapture(captureSize);
        const int overscroll = 40;
        var items = ArtifactGridLayout.CellsInRoi(captureSize, roi)
            .Take(16)
            .Select(cell => cell.Rect)
            .Select(rect => rect.Y >= overscroll
                ? new Rect(rect.X, rect.Y - overscroll, rect.Width, rect.Height)
                : new Rect(rect.X, 0, rect.Width, rect.Height - (overscroll - rect.Y)))
            .ToArray();

        Assert.False(ArtifactGridAlignmentPlanner.IsAligned(
            items, captureSize, roi, 8));
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

    [Theory]
    [InlineData(1.0, 100)]
    [InlineData(8.0, 100)]
    [InlineData(10.0, 100)]
    [InlineData(25.0, 100)]
    public void VerifiedFastRowBatching_StaysInsideThePerRowScrollBudget(
        double averageInputsPerRow,
        int expectedMilliseconds)
    {
        Assert.Equal(expectedMilliseconds,
            ArtifactRowScrollPlanner.EstimatedVerificationDelay(
                averageInputsPerRow));
        Assert.InRange(expectedMilliseconds, 0, 100);
    }

    [Fact]
    public void CalibrationAndFastVerification_StayInsideOnePointFiveSecondsPerRow()
    {
        Assert.InRange(
            ArtifactRowScrollPlanner.CalibrationInputLimit
                * ArtifactRowScrollPlanner.CalibrationDelayMilliseconds,
            0,
            1_500);
        Assert.InRange(
            ArtifactRowScrollPlanner.MaximumVerificationDelayMilliseconds,
            0,
            1_500);
        Assert.Equal(1_500, ArtifactRowScrollPlanner.PerRowBudgetMilliseconds);
    }

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(4.0, 2)]
    [InlineData(5.0, 3)]
    [InlineData(7.0, 5)]
    [InlineData(9.0, 7)]
    public void VerifiedFastRowBatching_PreadvancesBeforeSingleInputVerification(
        double averageInputsPerRow,
        int expectedPreadvanceInputs)
    {
        Assert.Equal(expectedPreadvanceInputs,
            ArtifactRowScrollPlanner.FastPreadvanceInputs(
                averageInputsPerRow));
    }

}
