using System;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>只在已确认红色残段右侧检查有边界暗槽；不搜索全屏暗色，不识别文字。</summary>
internal static class DepletedHealthBarReader
{
    internal static int ReadTrackWidth(Mat source, EnemySeekVisual red, int minimumWidth, out string reason)
    {
        reason = "source-or-edge";
        if (source.Type() != MatType.CV_8UC3 || red.Width <= 0 || red.Height < 2 ||
            red.X < 0 || red.Y < 1 || red.X + red.Width >= source.Width || red.Y + red.Height + 1 >= source.Height)
            return 0;
        var start = red.X + red.Width;
        var middle = red.Y + red.Height / 2;
        var anchor = source.At<Vec3b>(middle, start);
        var limit = Math.Min(source.Width, red.X + Math.Min(320, red.Height * 40));
        var x = start;
        for (; x < limit; x++)
        {
            var center = source.At<Vec3b>(middle, x);
            if (!IsDark(center) || MaximumDifference(center, anchor) > 24) break;
            var top = source.At<Vec3b>(red.Y + 1, x);
            var bottom = source.At<Vec3b>(red.Y + red.Height - 2, x);
            if (MaximumDifference(center, top) > 18 || MaximumDifference(center, bottom) > 18) break;
            // 不能只凭黑背景延长：每列都须有上下边缘，而非单纯的暗色像素。
            if (ColorDistance(center, source.At<Vec3b>(red.Y - 1, x)) < 22 ||
                ColorDistance(center, source.At<Vec3b>(red.Y + red.Height + 1, x)) < 22) break;
        }
        reason = x == limit ? "right-edge-unconfirmed" : "short-or-discontinuous-track";
        if (x == limit || x - start < Math.Max(8, red.Height * 3) || x - red.X < minimumWidth)
        {
            // 红色分量不包括抗锯齿边框；暗槽又会透出背景渐变，不能要求全长与首列同色。
            // 只在原红段紧邻的小ROI内找有上下边界和右端的槽，不扩大红色/几何准入。
            var padding = Math.Max(1, (int)Math.Round(source.Height * 2 / 1080d));
            for (var topPad = 0; topPad <= padding; topPad++)
            for (var bottomPad = 0; bottomPad <= padding; bottomPad++)
            {
                var width = ReadAntialiasedTrack(source, red, minimumWidth, limit, topPad, bottomPad);
                if (width > 0) { reason = "accepted-bounded-antialias-track"; return width; }
            }
            return 0;
        }
        reason = "accepted";
        return x - red.X;
    }

    private static int ReadAntialiasedTrack(Mat source, EnemySeekVisual red, int minimumWidth,
        int limit, int topPad, int bottomPad)
    {
        var top = red.Y - topPad;
        var bottom = red.Y + red.Height + bottomPad;
        if (top < 1 || bottom >= source.Height) return 0;
        var start = red.X + red.Width;
        var middle = red.Y + red.Height / 2;
        var previous = source.At<Vec3b>(middle, start);
        var supported = 0;
        var gaps = 0;
        for (var x = start; x < limit; x++)
        {
            var center = source.At<Vec3b>(middle, x);
            var end = !IsDark(center) || MaximumDifference(center, previous) > 24;
            if (end)
                return supported >= Math.Max(8, red.Height * 3) && x - red.X >= minimumWidth &&
                    supported * 100 >= (x - start) * 85 ? x - red.X : 0;
            var bounded = MaximumDifference(center, source.At<Vec3b>(top + 1, x)) <= 32 &&
                MaximumDifference(center, source.At<Vec3b>(bottom - 2, x)) <= 32 &&
                ColorDistance(center, source.At<Vec3b>(top - 1, x)) >= 22 &&
                ColorDistance(center, source.At<Vec3b>(bottom, x)) >= 22;
            if (bounded) { supported++; gaps = 0; }
            else if (++gaps > 2) return 0;
            previous = center;
        }
        return 0; // 达到搜索边界不证明存在槽的右端。
    }

    private static bool IsDark(Vec3b bgr) => bgr.Item0 <= 128 && bgr.Item1 <= 104 && bgr.Item2 <= 104 &&
        Math.Max(bgr.Item0, Math.Max(bgr.Item1, bgr.Item2)) - Math.Min(bgr.Item0, Math.Min(bgr.Item1, bgr.Item2)) <= 64;

    private static int MaximumDifference(Vec3b a, Vec3b b) => Math.Max(Math.Abs(a.Item0 - b.Item0),
        Math.Max(Math.Abs(a.Item1 - b.Item1), Math.Abs(a.Item2 - b.Item2)));

    private static int ColorDistance(Vec3b a, Vec3b b) =>
        Math.Abs(a.Item0 - b.Item0) + Math.Abs(a.Item1 - b.Item1) + Math.Abs(a.Item2 - b.Item2);
}
