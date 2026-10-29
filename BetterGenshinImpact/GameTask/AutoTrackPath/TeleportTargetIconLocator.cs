using System;
using System.Collections.Generic;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal static class TeleportTargetIconLocator
{
    // 与地图目标检测共用入口；不依赖 TaskContext、WPF 宿主或真实截图/输入。
    internal static List<Rect> FindVisibleWaypoints(Mat searchGrey, Mat templateGrey, double threshold)
    {
        var icons = MatchTemplateHelper.MatchOnePicForOnePic(searchGrey, templateGrey, null, threshold);
        var outlines = new List<(Rect Rect, double Score)>();
        var previousWidth = 0;
        // 只覆盖原图回放确认的30、31、32px尺度；其他捕获尺寸按原模板宽度等比换算。
        for (var referenceWidth = 30; referenceWidth <= 32; referenceWidth++)
        {
            var width = (int)Math.Round(templateGrey.Width * referenceWidth / 30d);
            if (width == previousWidth) continue;
            previousWidth = width;
            var height = (int)Math.Round(templateGrey.Height * width / (double)templateGrey.Width);
            using var scaled = new Mat();
            Cv2.Resize(templateGrey, scaled, new Size(width, height), 0, 0, InterpolationFlags.Linear);
            // 匹配完整白色图形及暗边，不用角色遮挡掩码补全图形。
            using var glyph = new Mat();
            Cv2.Threshold(scaled, glyph, 140, 255, ThresholdTypes.Binary);
            var border = Math.Max(1, (int)Math.Round(2d * width / 30d));
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(border * 2 + 1, border * 2 + 1));
            Cv2.Dilate(glyph, glyph, kernel);
            foreach (var match in MatchTemplateHelper.FindMatches(searchGrey, scaled,
                         TemplateMatchModes.CCoeffNormed, glyph, Math.Max(threshold, 0.85), -1))
            {
                if (!double.IsFinite(match.Score)) continue;
                outlines.Add((new Rect(match.Location.X, match.Location.Y, width, height), match.Score));
            }
        }

        // 各尺度按得分保留同一图标的最佳轮廓；原整模板已收录的图标保持原位。
        outlines.Sort((left, right) => right.Score.CompareTo(left.Score));
        foreach (var outline in outlines)
        {
            if (!icons.Exists(icon => OverlapsSameIcon(icon, outline.Rect)))
                icons.Add(outline.Rect);
        }
        return icons;
    }

    private static bool OverlapsSameIcon(Rect left, Rect right)
    {
        var width = Math.Max(0, Math.Min(left.Right, right.Right) - Math.Max(left.X, right.X));
        var height = Math.Max(0, Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Y, right.Y));
        var intersection = (double)width * height;
        var union = (double)left.Width * left.Height + (double)right.Width * right.Height - intersection;
        return union > 0 && intersection / union >= 0.5;
    }
}
