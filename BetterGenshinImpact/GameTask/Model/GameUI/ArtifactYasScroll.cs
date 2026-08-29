using OpenCvSharp;
using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.Model.GameUI;

internal sealed class ArtifactRowScrollDetector
{
    private const int ColorDistanceThreshold = 10;
    private readonly Vec3b initialColor;
    private bool hasChanged;

    internal ArtifactRowScrollDetector(Vec3b initialColor)
    {
        this.initialColor = initialColor;
    }

    internal bool Observe(Vec3b color)
    {
        var isInitialColor = IsNear(this.initialColor, color);
        if (!this.hasChanged)
        {
            this.hasChanged = !isInitialColor;
            return false;
        }

        return isInitialColor;
    }

    internal static bool IsNear(Vec3b left, Vec3b right)
    {
        return ColorDistance(left, right) <= ColorDistanceThreshold;
    }

    private static double ColorDistance(Vec3b left, Vec3b right)
    {
        var blue = left.Item0 - right.Item0;
        var green = left.Item1 - right.Item1;
        var red = left.Item2 - right.Item2;
        return Math.Sqrt(blue * blue + green * green + red * red);
    }
}

internal static class ArtifactRowScrollPlanner
{
    internal static int RowsToScroll(int totalRows, int visibleRows, int scrolledRows)
    {
        if (totalRows <= 0) throw new ArgumentOutOfRangeException(nameof(totalRows));
        if (visibleRows <= 0) throw new ArgumentOutOfRangeException(nameof(visibleRows));
        if (scrolledRows < 0) throw new ArgumentOutOfRangeException(nameof(scrolledRows));

        return Math.Min(visibleRows, Math.Max(0, totalRows - visibleRows - scrolledRows));
    }

    internal static int EstimateInputCount(double averageInputsPerRow, int rows)
    {
        if (averageInputsPerRow <= 0) throw new ArgumentOutOfRangeException(nameof(averageInputsPerRow));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));

        return Math.Max(0, (int)Math.Round(
            averageInputsPerRow * rows - 2,
            MidpointRounding.AwayFromZero));
    }

}

internal static class ArtifactGridLayout
{
    private const double FirstCellX = 99;
    private const double FirstCellY = 149.5;
    private const double CellWidth = 102;
    private const double CellHeight = 126;
    private const double HorizontalGap = 20;
    private const double VerticalGap = 20;
    private const double ScrollFlagX = 271.1;
    private const double ScrollFlagY = 138.3;

    internal static IReadOnlyList<GridCell> CellsInRoi(Size captureSize, Rect roi)
    {
        var cells = new List<GridCell>(40);

        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 8; column++)
            {
                var absolute = ArtifactUiCoordinateMapper.ToCaptureRect(
                    captureSize,
                    FirstCellX + column * (CellWidth + HorizontalGap),
                    FirstCellY + row * (CellHeight + VerticalGap),
                    CellWidth,
                    CellHeight);
                cells.Add(new GridCell(new Rect(
                    absolute.X - roi.X,
                    absolute.Y - roi.Y,
                    absolute.Width,
                    absolute.Height))
                {
                    RowNum = row,
                    ColNum = column
                });
            }
        }

        return cells;
    }

    internal static Point ScrollFlagPosition(Size captureSize)
    {
        return ArtifactUiCoordinateMapper.ToCapturePoint(
            captureSize, ScrollFlagX, ScrollFlagY);
    }

    internal static Vec3b ReadBgr(Mat capture, Point position)
    {
        if (position.X < 0 || position.X >= capture.Width ||
            position.Y < 0 || position.Y >= capture.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        return capture.Channels() switch
        {
            4 => ToBgr(capture.At<Vec4b>(position.Y, position.X)),
            3 => capture.At<Vec3b>(position.Y, position.X),
            1 => ToBgr(capture.At<byte>(position.Y, position.X)),
            var channels => throw new InvalidOperationException(
                $"不支持的圣遗物翻页截图通道数：{channels}")
        };
    }

    private static Vec3b ToBgr(Vec4b color)
    {
        return new Vec3b(color.Item0, color.Item1, color.Item2);
    }

    private static Vec3b ToBgr(byte color)
    {
        return new Vec3b(color, color, color);
    }
}
