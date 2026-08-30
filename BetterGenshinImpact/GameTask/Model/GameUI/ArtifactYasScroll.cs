using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;

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

    internal bool HasObservedChange => this.hasChanged;

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
    internal const int CalibrationDelayMilliseconds = 80;
    internal const int CalibrationInputLimit = 12;
    internal const int PerRowBudgetMilliseconds = 1_500;
    internal const int VerificationDelayMilliseconds = 20;
    internal const int MaximumVerificationSamples = 5;
    internal const int MaximumVerificationDelayMilliseconds =
        VerificationDelayMilliseconds * MaximumVerificationSamples;

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

    internal static int FastPreadvanceInputs(double averageInputsPerRow)
    {
        if (averageInputsPerRow <= 0)
            throw new ArgumentOutOfRangeException(nameof(averageInputsPerRow));
        return Math.Max(0, (int)Math.Floor(averageInputsPerRow) - 2);
    }

    internal static int EstimatedVerificationDelay(double averageInputsPerRow)
    {
        _ = FastPreadvanceInputs(averageInputsPerRow);
        return MaximumVerificationDelayMilliseconds;
    }

}

internal static class ArtifactGridAlignmentPlanner
{
    internal static bool IsAligned(
        IEnumerable<Rect> items,
        Size captureSize,
        Rect roi,
        int columns)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (captureSize.Width <= 0 || captureSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(captureSize));
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));

        var template = ArtifactGridLayout.CellsInRoi(captureSize, roi);
        if (template.Count < columns * 2) return false;
        var firstRow = template[0].Rect;
        var secondRow = template[columns].Rect;
        var yTolerance = Math.Max(4, (int)Math.Round(firstRow.Height * 0.12));
        var heightTolerance = Math.Max(4, (int)Math.Round(firstRow.Height * 0.10));
        var candidates = items.Where(item =>
            Math.Abs(item.Height - firstRow.Height) <= heightTolerance).ToArray();
        var minimumItemsPerRow = Math.Max(1, columns - 1);

        return candidates.Count(item =>
                Math.Abs(item.Y - firstRow.Y) <= yTolerance)
            >= minimumItemsPerRow
            && candidates.Count(item =>
                Math.Abs(item.Y - secondRow.Y) <= yTolerance)
            >= minimumItemsPerRow;
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

        var radius = Math.Max(1, (int)Math.Round(capture.Width / 1600d * 2));
        var left = Math.Max(0, position.X - radius);
        var top = Math.Max(0, position.Y - radius);
        var right = Math.Min(capture.Width, position.X + radius + 1);
        var bottom = Math.Min(capture.Height, position.Y + radius + 1);
        using var patch = new Mat(capture, new Rect(
            left, top, right - left, bottom - top));
        var mean = Cv2.Mean(patch);
        return capture.Channels() switch
        {
            4 or 3 => new Vec3b(
                (byte)Math.Round(mean.Val0),
                (byte)Math.Round(mean.Val1),
                (byte)Math.Round(mean.Val2)),
            1 => ToBgr((byte)Math.Round(mean.Val0)),
            var channels => throw new InvalidOperationException(
                $"不支持的圣遗物翻页截图通道数：{channels}")
        };
    }

    private static Vec3b ToBgr(byte color)
    {
        return new Vec3b(color, color, color);
    }
}
