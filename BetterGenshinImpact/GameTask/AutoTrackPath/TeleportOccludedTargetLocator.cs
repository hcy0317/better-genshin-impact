using System;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal static class TeleportOccludedTargetLocator
{
    // 角色标记不是传送图标。只在邻点已精确配准后，用未遮挡的图标及暗边补充目标识别。
    internal static bool TryLocate(Mat frame, Mat templateGrey, double x, double y,
        double scale, int anchorCount, double uncertainty, out Rect iconRect)
    {
        iconRect = default;
        if (anchorCount < 3 || !double.IsFinite(scale) || scale <= 0 ||
            !double.IsFinite(uncertainty) || uncertainty < 0 || uncertainty > 10 * scale ||
            !double.IsFinite(x) || !double.IsFinite(y) || frame.Empty() || templateGrey.Empty())
            return false;

        var radius = (int)Math.Ceiling(Math.Min(12 * scale, uncertainty + 3 * scale));
        var left = (int)Math.Round(x - templateGrey.Width / 2d);
        var top = (int)Math.Round(y - templateGrey.Height / 2d);
        var searchRect = new Rect(left - radius, top - radius,
            templateGrey.Width + radius * 2, templateGrey.Height + radius * 2);
        if (searchRect.X < 0 || searchRect.Y < 0 || searchRect.Right > frame.Width || searchRect.Bottom > frame.Height)
            return false;

        using var source = new Mat(frame, searchRect);
        using var bgr = new Mat();
        if (source.Channels() == 4)
            Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);
        else
            source.CopyTo(bgr);
        using var grey = new Mat();
        Cv2.CvtColor(bgr, grey, ColorConversionCodes.BGR2GRAY);
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        using var overlay = new Mat();
        Cv2.InRange(hsv, new Scalar(80, 80, 100), new Scalar(110, 255, 255), overlay);
        var edge = Math.Max(1, (int)Math.Round(scale));
        using var overlayKernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(edge * 2 + 1, edge * 2 + 1));
        Cv2.Dilate(overlay, overlay, overlayKernel);

        using var glyph = new Mat();
        Cv2.Threshold(templateGrey, glyph, 140, 255, ThresholdTypes.Binary);
        var border = Math.Max(1, (int)Math.Round(2 * scale));
        using var glyphKernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(border * 2 + 1, border * 2 + 1));
        Cv2.Dilate(glyph, glyph, glyphKernel);
        using var white = new Mat();
        Cv2.Threshold(templateGrey, white, 180, 255, ThresholdTypes.Binary);
        var whiteCount = Cv2.CountNonZero(white);
        if (whiteCount == 0) return false;

        var area = templateGrey.Width * templateGrey.Height;
        var bestScore = 0.85;
        using var visible = new Mat();
        using var visibleWhite = new Mat();
        using var response = new Mat();
        for (var dy = 0; dy <= radius * 2; dy++)
        for (var dx = 0; dx <= radius * 2; dx++)
        {
            if (Math.Pow(dx - radius, 2) + Math.Pow(dy - radius, 2) > radius * radius) continue;
            var local = new Rect(dx, dy, templateGrey.Width, templateGrey.Height);
            var candidate = new Rect(searchRect.X + dx, searchRect.Y + dy, local.Width, local.Height);
            if (x < candidate.X || x >= candidate.Right || y < candidate.Y || y >= candidate.Bottom) continue;
            using var occlusion = new Mat(overlay, local);
            var hidden = Cv2.CountNonZero(occlusion);
            if (hidden < area * 0.15 || hidden > area * 0.75) continue;
            Cv2.BitwiseNot(occlusion, visible);
            Cv2.BitwiseAnd(visible, glyph, visible);
            Cv2.BitwiseAnd(visible, white, visibleWhite);
            if (Cv2.CountNonZero(visibleWhite) < Math.Max(20 * scale * scale, whiteCount * 0.2)) continue;

            using var pixels = new Mat(grey, local);
            Cv2.MatchTemplate(pixels, templateGrey, response, TemplateMatchModes.CCoeffNormed, visible);
            var score = response.At<float>(0, 0);
            if (!float.IsFinite(score) || score < bestScore) continue;
            bestScore = score;
            iconRect = candidate;
        }

        return iconRect.Width > 0;
    }
}
