using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;
using System.Diagnostics;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactYasScrollTests
{
    [Fact]
    public void GridScrollerUsesOneRulerCalibrationAndOnePageScrollStateMachine()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "Model",
            "GameUI",
            "GridScroller.cs"));

        Assert.Contains("MeasureScrollPixelsPerInputAsync", source, StringComparison.Ordinal);
        Assert.Contains("ScrollPageAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ScrollOneArtifactRowAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("快速推进第", source, StringComparison.Ordinal);
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
    public void PageOffsetFeedbackRequestsOnlyTheMissingWheelInputs()
    {
        var captureSize = new Size(1920, 1080);
        var roi = GridParams.ArtifactRoiForCapture(captureSize);
        var shifted = ArtifactGridLayout.CellsInRoi(captureSize, roi)
            .Take(24)
            .Select(cell => cell.Rect)
            .Select(rect => new Rect(rect.X, rect.Y + 36, rect.Width, rect.Height))
            .ToArray();

        var offset = ArtifactGridAlignmentPlanner.VerticalOffsetPixels(
            shifted, captureSize, roi, columns: 8);

        Assert.Equal(36, offset);
        Assert.Equal(2, ArtifactGridAlignmentPlanner.CorrectionInputs(
            offset!.Value, pixelsPerInput: 18));
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

    [Fact]
    public void YasPixelPlanCarriesRoundingResidualAcrossPages()
    {
        var first = YasPixelScrollPlanner.CreatePlan(
            rowPitchPixels: 146,
            pixelsPerInput: 15,
            residualPixels: 0,
            rows: 5);
        var second = YasPixelScrollPlanner.CreatePlan(
            rowPitchPixels: 146,
            pixelsPerInput: 15,
            residualPixels: first.ResidualPixels,
            rows: 5);

        Assert.Equal(49, first.InputCount);
        Assert.Equal(-5, first.ResidualPixels, 6);
        Assert.Equal(48, second.InputCount);
        Assert.Equal(5, second.ResidualPixels, 6);
        Assert.Equal(
            146 * 10,
            (first.InputCount + second.InputCount) * 15
                + second.ResidualPixels,
            6);
    }

    [Fact]
    public void YasPixelPlanDoesNotDriftAcrossTenPages()
    {
        double residual = 0;
        var totalInputs = 0;
        for (var page = 0; page < 10; page++)
        {
            var plan = YasPixelScrollPlanner.CreatePlan(
                rowPitchPixels: 146,
                pixelsPerInput: 15,
                residualPixels: residual,
                rows: 5);
            totalInputs += plan.InputCount;
            residual = plan.ResidualPixels;
        }

        Assert.Equal(146 * 50, totalInputs * 15 + residual, 6);
        Assert.InRange(Math.Abs(residual), 0, 7.5);
    }

    [Fact]
    public void YasRulerFindsTheCumulativePixelShift()
    {
        var before = Enumerable.Range(0, 40)
            .Select(index => new Vec3b(
                (byte)(index * 3),
                (byte)(index * 5),
                (byte)(index * 7)))
            .ToArray();
        var after = Enumerable.Repeat(new Vec3b(250, 250, 250), 7)
            .Concat(before.Take(before.Length - 7))
            .ToArray();

        Assert.Equal(7, YasPixelScrollPlanner.FindRulerShift(
            before, after, maximumShift: 12));
        Assert.Equal(20, YasPixelScrollPlanner.FirstPageInputIntervalMilliseconds);
        Assert.Equal(2, YasPixelScrollPlanner.FastInputIntervalMilliseconds);
    }

    [Fact]
    public void FastWheelInputsUseHighResolutionPacingInsteadOfTimerQuantizedDelay()
    {
        Assert.True(YasScrollInputPacer.UsesHighResolutionPacing(
            YasPixelScrollPlanner.FastInputIntervalMilliseconds));
        Assert.False(YasScrollInputPacer.UsesHighResolutionPacing(
            YasPixelScrollPlanner.FirstPageInputIntervalMilliseconds));
    }

    [Fact]
    public async Task FastWheelInputPacingHonorsCancellationBeforeWaiting()
    {
        using var cancellation = new CancellationTokenSource();
        using var pacer = new YasScrollInputPacer();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pacer.DelayAsync(
                YasPixelScrollPlanner.FastInputIntervalMilliseconds,
                cancellation.Token));
    }

    [Fact]
    public async Task OnePageOfFastWheelInputPacingAvoidsTimerQuantization()
    {
        var timer = Stopwatch.StartNew();
        using var pacer = new YasScrollInputPacer();
        for (var input = 0; input < 25; input++)
        {
            await pacer.DelayAsync(
                YasPixelScrollPlanner.FastInputIntervalMilliseconds,
                CancellationToken.None);
        }

        Assert.InRange(timer.ElapsedMilliseconds, 35, 250);
    }

    [Fact]
    public void YasRulerStabilityAcceptsNoiseButRejectsMovement()
    {
        var baseline = Enumerable.Range(0, 30)
            .Select(index => new Vec3b(
                (byte)(20 + index),
                (byte)(40 + index),
                (byte)(60 + index)))
            .ToArray();
        var noisy = baseline
            .Select(color => new Vec3b(
                (byte)(color.Item0 + 1),
                color.Item1,
                color.Item2))
            .ToArray();
        var shifted = new[] { new Vec3b(250, 250, 250) }
            .Concat(baseline.Take(29))
            .ToArray();

        Assert.True(YasPixelScrollPlanner.IsRulerStable(baseline, noisy));
        Assert.False(YasPixelScrollPlanner.IsRulerStable(baseline, shifted));
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterGenshinImpact.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate BetterGenshinImpact.sln.");
    }

}
