using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>构装体隐身部件的白/金血条必须与同帧蓝色倒三角共同出现；不从部件消失推断战斗结束。</summary>
internal static class CombatComponentHealthBarReader
{
    internal static IReadOnlyList<Rect> Read(Mat source)
    {
        var result = new List<Rect>();
        if (source.Empty() || source.Channels() != 3) return result;
        var scale = source.Height / 1080d;
        using var hsv = new Mat();
        using var blue = new Mat();
        Cv2.CvtColor(source, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(80, 60, 150), new Scalar(115, 255, 255), blue);
        Cv2.FindContours(blue, out Point[][] markers, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        foreach (var contour in markers)
        {
            var marker = Cv2.BoundingRect(contour);
            var centerX = marker.X + marker.Width / 2;
            if (marker.Width < 24 * scale || marker.Width > 70 * scale ||
                marker.Height < 14 * scale || marker.Height > 44 * scale ||
                marker.Width / (double)marker.Height is < 1.1 or > 2.1 ||
                centerX < source.Width * .08 || centerX > source.Width * .82 ||
                marker.Y < source.Height * .15 || marker.Bottom > source.Height * .85 ||
                Cv2.ContourArea(contour) / (marker.Width * marker.Height) is < .25 or > .85 ||
                !PointsDown(blue, marker)) continue;

            // 原始5155：蓝标1066,496,42x27；白/金剩余量1038,533,39x5。
            // 只检查标记正下方的短带，不把白色场景边缘/伤害数字提升为血条。
            var left = Math.Max(0, centerX - (int)Math.Ceiling(120 * scale));
            var top = marker.Bottom + Math.Max(1, (int)(3 * scale));
            var bounds = new Rect(left, top,
                Math.Min(source.Width - left, (int)Math.Ceiling(240 * scale)),
                Math.Min(source.Height - top, Math.Max(1, (int)Math.Ceiling(26 * scale))));
            if (bounds.Height <= 0 || bounds.Width <= 0) continue;
            using var band = new Mat(hsv, bounds);
            using var white = new Mat();
            using var gold = new Mat();
            using var fill = new Mat();
            Cv2.InRange(band, new Scalar(0, 0, 220), new Scalar(180, 40, 255), white);
            Cv2.InRange(band, new Scalar(15, 60, 215), new Scalar(28, 180, 255), gold);
            Cv2.BitwiseOr(white, gold, fill);
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();
            var count = Cv2.ConnectedComponentsWithStats(fill, labels, stats, centroids,
                PixelConnectivity.Connectivity4, MatType.CV_32S);
            for (var i = 1; i < count; i++)
            {
                var bar = new Rect(bounds.X + stats.At<int>(i, 0), bounds.Y + stats.At<int>(i, 1),
                    stats.At<int>(i, 2), stats.At<int>(i, 3));
                var area = stats.At<int>(i, 4);
                if (bar.Width < 24 * scale || bar.Width > 180 * scale ||
                    bar.Height < Math.Max(2, 3 * scale) || bar.Height > 12 * scale ||
                    bar.Width < bar.Height * 4 || area < bar.Width * bar.Height * .75 ||
                    Math.Abs(bar.X + bar.Width / 2 - centerX) > Math.Max(30 * scale, bar.Width * .75)) continue;
                if (!result.Contains(bar)) result.Add(bar);
            }
        }
        return result;
    }

    private static bool PointsDown(Mat mask, Rect marker)
    {
        (int Width, double Center) Span(int y)
        {
            var left = marker.Right;
            var right = marker.Left - 1;
            for (var x = marker.Left; x < marker.Right; x++)
                if (mask.At<byte>(y, x) != 0) { left = Math.Min(left, x); right = x; }
            return (Math.Max(0, right - left + 1), (left + right) / 2d);
        }
        var upper = Span(marker.Y + marker.Height / 4);
        var lower = Span(marker.Y + marker.Height * 3 / 4);
        return upper.Width >= marker.Width * .60 && lower.Width >= marker.Width * .10 &&
            lower.Width <= marker.Width * .60 && Math.Abs(upper.Center - lower.Center) <= marker.Width * .20;
    }
}
