using OpenCvSharp;
using System;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static class ArtifactGridLockDetector
{
    private static readonly Vec3b YasLockColor = new(117, 138, 255);

    internal static bool IsLocked(Mat cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.Empty() || cell.Channels() < 3) return false;

        var probe = ProbeBounds(cell.Size());
        for (var y = probe.Top; y < probe.Bottom; y++)
        {
            for (var x = probe.Left; x < probe.Right; x++)
            {
                var color = cell.Channels() == 4
                    ? ToBgr(cell.At<Vec4b>(y, x))
                    : cell.At<Vec3b>(y, x);
                if (ColorDistance(color, YasLockColor) < 30)
                {
                    return true;
                }
            }
        }
        return false;
    }

    internal static double VisualSignature(Mat cell)
    {
        var probe = ProbeBounds(cell.Size());
        using var region = cell.SubMat(probe);
        var mean = Cv2.Mean(region);
        return mean.Val0 + mean.Val1 + mean.Val2;
    }

    private static Rect ProbeBounds(Size cellSize)
    {
        var centerX = (int)Math.Round(cellSize.Width * 12.0 / 102.0);
        var centerY = (int)Math.Round(cellSize.Height * 14.0 / 126.0);
        var radiusX = Math.Max(1, (int)Math.Round(cellSize.Width * 2.0 / 102.0));
        var radiusY = Math.Max(1, (int)Math.Round(cellSize.Height * 10.0 / 126.0));
        var left = Math.Clamp(centerX - radiusX, 0, cellSize.Width - 1);
        var top = Math.Clamp(centerY - radiusY, 0, cellSize.Height - 1);
        var right = Math.Clamp(centerX + radiusX + 1, left + 1, cellSize.Width);
        var bottom = Math.Clamp(centerY + radiusY + 1, top + 1, cellSize.Height);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static double ColorDistance(Vec3b left, Vec3b right)
    {
        var blue = left.Item0 - right.Item0;
        var green = left.Item1 - right.Item1;
        var red = left.Item2 - right.Item2;
        return Math.Sqrt(blue * blue + green * green + red * red);
    }

    private static Vec3b ToBgr(Vec4b color) =>
        new(color.Item0, color.Item1, color.Item2);
}
