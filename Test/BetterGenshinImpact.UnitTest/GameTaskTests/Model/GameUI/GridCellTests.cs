using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.Model.GameUI;

public class GridCellTests
{
    [Fact]
    public void FillMissingGridCells_DuplicateRecognitionForOneCoordinateMustNotThrowOrRemainDuplicated()
    {
        List<GridCell> cells =
        [
            CreateCell(0, 0, new Rect(0, 0, 100, 120)),
            CreateCell(0, 0, new Rect(1, 1, 98, 118)),
            CreateCell(1, 0, new Rect(110, 0, 100, 120)),
            CreateCell(0, 1, new Rect(0, 130, 100, 120))
        ];

        GridCell.FillMissingGridCells(ref cells);

        Assert.Equal(4, cells.Count);
        Assert.Equal(4, cells.Select(cell => (cell.ColNum, cell.RowNum)).Distinct().Count());
        Assert.Contains(cells, cell => cell.ColNum == 1 && cell.RowNum == 1 && cell.IsPhantom);
    }

    private static GridCell CreateCell(int col, int row, Rect rect)
    {
        return new GridCell(rect)
        {
            ColNum = col,
            RowNum = row
        };
    }
}
