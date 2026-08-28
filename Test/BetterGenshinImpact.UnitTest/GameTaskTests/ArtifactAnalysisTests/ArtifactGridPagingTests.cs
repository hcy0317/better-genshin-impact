using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactGridPagingTests
{
    [Fact]
    public void SelectFastScrollItems_FirstPageReturnsTheVisibleCapacity()
    {
        var cells = Cells(rows: 5, columns: 8);

        var selected = GridScreen.GridEnumerator.SelectFastScrollItems(
            cells, columns: 8, visibleRows: 5, fastScrollRows: 5,
            emittedItems: 0, totalItems: 1125, firstPage: true);

        Assert.Equal(40, selected.Count);
        Assert.Equal(new Rect(0, 0, 10, 10), selected[0]);
        Assert.Equal(new Rect(70, 400, 10, 10), selected[^1]);
    }

    [Fact]
    public void SelectFastScrollItems_LaterPageReturnsTheNewFiveRows()
    {
        var cells = Cells(rows: 5, columns: 8);

        var selected = GridScreen.GridEnumerator.SelectFastScrollItems(
            cells, columns: 8, visibleRows: 5, fastScrollRows: 5,
            emittedItems: 40, totalItems: 1125, firstPage: false);

        Assert.Equal(40, selected.Count);
        Assert.Equal(new Rect(0, 0, 10, 10), selected[0]);
        Assert.Equal(new Rect(70, 400, 10, 10), selected[^1]);
    }

    [Fact]
    public void SelectFastScrollItems_FinalPageUsesOnlyTheRemainingItems()
    {
        var cells = Cells(rows: 5, columns: 8);

        var selected = GridScreen.GridEnumerator.SelectFastScrollItems(
            cells, columns: 8, visibleRows: 5, fastScrollRows: 5,
            emittedItems: 1120, totalItems: 1125, firstPage: false);

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
                cells, columns: 8, visibleRows: 5, fastScrollRows: 5,
                emittedItems: 1120, totalItems: 1125, firstPage: false));

        Assert.Contains("底部新行", exception.Message);
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
}
