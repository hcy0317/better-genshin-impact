using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using BetterGenshinImpact.GameTask.Model.GameUI;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactCharacterRosterPaginationTests
{
    [Fact]
    public void FullPageAdvanceSelectsAllSixNewRows()
    {
        var tracker = new ArtifactCharacterPageTracker();
        tracker.Commit(Rows(1, 6));

        var selected = tracker.SelectUnprocessedRows(Rows(7, 6));

        Assert.Equal(6, selected.Count);
    }

    [Fact]
    public void FinalShortPageSelectsOnlyRowsAfterTheOverlap()
    {
        var tracker = new ArtifactCharacterPageTracker();
        tracker.Commit(Rows(1, 6));

        var selected = tracker.SelectUnprocessedRows(Rows(5, 5));

        Assert.Equal([7UL, 8UL, 9UL], selected.Select(RowId));
    }

    [Fact]
    public void MotionlessScrollSelectsNothingAndSignalsTheEnd()
    {
        var tracker = new ArtifactCharacterPageTracker();
        tracker.Commit(Rows(1, 6));

        var selected = tracker.SelectUnprocessedRows(Rows(1, 6));

        Assert.Empty(selected);
    }

    [Fact]
    public void PartialScrollSkipsTheOldTopRowsAndSelectsOnlyNewBottomRows()
    {
        var tracker = new ArtifactCharacterPageTracker();
        tracker.Commit(Rows(1, 6));

        var selected = tracker.SelectUnprocessedRows(Rows(4, 6));

        Assert.Equal([7UL, 8UL, 9UL], selected.Select(RowId));
    }

    [Fact]
    public void BottomClampedAdvanceSkipsThePreviouslyClickedPartialSixthRow()
    {
        var tracker = new ArtifactCharacterPageTracker();
        tracker.Commit(Rows(1, 6));

        var selected = tracker.SelectUnprocessedRows(Rows(6, 6));

        Assert.Equal([7UL, 8UL, 9UL, 10UL, 11UL],
            selected.Select(RowId));
    }

    [Fact]
    public void ScrollResultStartRowDirectlySelectsOnlyActuallyAdvancedRows()
    {
        var selected = ArtifactCharacterPageTracker.SelectFromStartRow(
            Rows(6, 6), startRow: 1);

        Assert.Equal([7UL, 8UL, 9UL, 10UL, 11UL],
            selected.Select(RowId));
    }

    [Fact]
    public void ScrollPlannerUsesTheCharacterUiDesignRowPitch()
    {
        Assert.Equal(6, ArtifactCharacterScrollPlanner.PageAdvanceRows);
        Assert.Equal(156, ArtifactCharacterScrollPlanner.RowPitchForGridHeight(917), 6);
        Assert.Equal(312, ArtifactCharacterScrollPlanner.RowPitchForGridHeight(1834), 6);
    }

    [Fact]
    public void CharacterScannerUsesRulerCalibrationAndOneWholePageScroll()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "ArtifactCharacterRosterScanner.cs"));

        Assert.Contains("pageTracker.Commit(stableRows)", source,
            StringComparison.Ordinal);
        Assert.Contains("MeasureScrollPixelsPerInputAsync", source,
            StringComparison.Ordinal);
        Assert.Contains("gridRoi.Right -", source, StringComparison.Ordinal);
        Assert.DoesNotContain("gridRoi.Right +", source, StringComparison.Ordinal);
        Assert.Contains("ArtifactCharacterScrollPlanner.PageAdvanceRows", source,
            StringComparison.Ordinal);
        Assert.Contains("ConsumeNextStartRow", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CalibrateFirstPageScrollAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("RemainingRowsToAdvance", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ArtifactCharacterScrollPlanner.AdvancedRows", source,
            StringComparison.Ordinal);
    }

    private static ArtifactCharacterPageRow[] Rows(ulong first, int count) =>
        Enumerable.Range(0, count)
            .Select(offset => first + (ulong)offset)
            .Select(id => new ArtifactCharacterPageRow(
                [new OpenCvSharp.Rect((int)id, 0, 1, 1)],
                [unchecked(id * 0x9E3779B97F4A7C15UL)]))
            .ToArray();

    private static ulong RowId(ArtifactCharacterPageRow row) => (ulong)row.Cards[0].X;

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName,
                    "BetterGenshinImpact.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate BetterGenshinImpact.sln.");
    }
}
