using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactGridPagingTests
{
    [Fact]
    public void FastArtifactPaginationHasOneCursorAndNoDuplicateLogicalCounters()
    {
        var root = FindRepoRoot();
        var gridScreen = File.ReadAllText(Path.Combine(
            root, "BetterGenshinImpact", "GameTask", "Model", "GameUI", "GridScreen.cs"));
        var gridScroller = File.ReadAllText(Path.Combine(
            root, "BetterGenshinImpact", "GameTask", "Model", "GameUI", "GridScroller.cs"));

        Assert.Contains("YasPaginationCursor", gridScreen, StringComparison.Ordinal);
        Assert.DoesNotContain("emittedItems", gridScreen, StringComparison.Ordinal);
        Assert.DoesNotContain("scrolledRows", gridScroller, StringComparison.Ordinal);
        Assert.Contains("ScrollRowsFastAsync", gridScroller, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectFastScrollItems_FirstPageReturnsTheVisibleCapacity()
    {
        var cells = Cells(rows: 5, columns: 8);

        var selected = GridScreen.GridEnumerator.SelectFastScrollItems(
            cells,
            columns: 8,
            new YasPageSlice(0, 0, 5, 40, false));

        Assert.Equal(40, selected.Count);
        Assert.Equal(new Rect(0, 0, 10, 10), selected[0]);
        Assert.Equal(new Rect(70, 400, 10, 10), selected[^1]);
    }

    [Fact]
    public void SelectFastScrollItems_LaterPageReturnsTheNewFiveRows()
    {
        var cells = Cells(rows: 5, columns: 8);

        var selected = GridScreen.GridEnumerator.SelectFastScrollItems(
            cells,
            columns: 8,
            new YasPageSlice(1, 0, 5, 40, false));

        Assert.Equal(40, selected.Count);
        Assert.Equal(new Rect(0, 0, 10, 10), selected[0]);
        Assert.Equal(new Rect(70, 400, 10, 10), selected[^1]);
    }

    [Fact]
    public void SelectFastScrollItems_FinalPageUsesOnlyTheRemainingItems()
    {
        var cells = Cells(rows: 5, columns: 8);

        var selected = GridScreen.GridEnumerator.SelectFastScrollItems(
            cells,
            columns: 8,
            new YasPageSlice(28, 4, 1, 5, true));

        Assert.Equal(5, selected.Count);
        Assert.Equal([0, 10, 20, 30, 40], selected.Select(rect => rect.X));
    }

    [Fact]
    public void SelectFastScrollItems_RejectsAnIncompleteFinalRow()
    {
        var cells = Cells(rows: 5, columns: 8)
            .Where(cell => cell.RowNum != 4 || cell.ColNum < 4)
            .ToArray();

        var exception = Assert.Throws<InvalidDataException>(() =>
            GridScreen.GridEnumerator.SelectFastScrollItems(
                cells,
                columns: 8,
                new YasPageSlice(28, 4, 1, 5, true)));

        Assert.Contains("cursor 指定格子", exception.Message);
    }

    [Fact]
    public void PostProcess_FastArtifactModeKeepsGeometryInferredCells()
    {
        using var grid = new Mat(
            new Size(880, 600),
            MatType.CV_8UC3,
            Scalar.Black);
        var rects = Cells(rows: 4, columns: 8)
            .Where(cell => cell.RowNum != 3 || cell.ColNum != 7)
            .Select(cell => new Rect(
                cell.ColNum * 110,
                cell.RowNum * 150,
                100,
                124))
            .ToArray();

        var normal = GridScreen.GridEnumerator.PostProcess(
            grid, rects, threshold: 20).ToArray();
        var fastArtifact = GridScreen.GridEnumerator.PostProcess(
            grid, rects, threshold: 20,
            validatePhantomBottomColor: false).ToArray();

        Assert.Equal(31, normal.Length);
        Assert.Equal(32, fastArtifact.Length);
        Assert.Contains(fastArtifact, cell => cell.RowNum == 3 && cell.ColNum == 7 && cell.IsPhantom);
    }

    private static GridCell[] Cells(int rows, int columns)
    {
        return Enumerable.Range(0, rows)
            .SelectMany(row => Enumerable.Range(0, columns)
                .Select(column => new GridCell(
                    new Rect(column * 10, row * 100, 10, 10))
                {
                    RowNum = row,
                    ColNum = column
                }))
            .ToArray();
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
