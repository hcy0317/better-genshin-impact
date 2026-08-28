using BetterGenshinImpact.GameTask.ArtifactAnalysis;

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

    private static ArtifactCharacterPageRow[] Rows(ulong first, int count) =>
        Enumerable.Range(0, count)
            .Select(offset => first + (ulong)offset)
            .Select(id => new ArtifactCharacterPageRow(
                [new OpenCvSharp.Rect((int)id, 0, 1, 1)],
                [unchecked(id * 0x9E3779B97F4A7C15UL)]))
            .ToArray();

    private static ulong RowId(ArtifactCharacterPageRow row) => (ulong)row.Cards[0].X;
}
