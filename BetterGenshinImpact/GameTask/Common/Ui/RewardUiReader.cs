using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;
using System;
using System.Linq;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal readonly record struct RewardObservation(CaptureFrameStamp Source, bool TitleFound,
    bool FooterFound, Rect CloseBounds)
{
    internal bool IsCandidate => TitleFound || FooterFound;
    internal bool CanDismiss => Source.IsKnown && TitleFound && FooterFound && CloseBounds.Width > 0 && CloseBounds.Height > 0;
    internal bool IsFor(CaptureFrameStamp source) => Source.IsKnown && Source == source;
}

internal static class RewardUiReader
{
    internal static RewardObservation Read(ImageRegion image, IOcrService ocr)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(ocr);
        var empty = new RewardObservation(image.FrameStamp, false, false, default);
        if (image.SrcMat.Empty() || image.Width * 9 != image.Height * 16) return empty;
        var scale = image.Width / 1920d;
        var titleRoi = Scaled(800, 260, 320, 80, scale);
        var footerRoi = Scaled(700, 710, 520, 85, scale);
        var imageBounds = new Rect(0, 0, image.Width, image.Height);
        if (!Inside(imageBounds, titleRoi) || !Inside(imageBounds, footerRoi)) return empty;

        var titles = Matches(image, ocr, "获得", titleRoi, scale);
        var footers = Matches(image, ocr, "点击空白区域继续", footerRoi, scale);
        // Multiple competing hits are candidate evidence only, not a click target.
        var close = titles.Length == 1 && footers.Length == 1 && titles[0].Bottom < footers[0].Top
            ? footers[0] : default;
        return new(image.FrameStamp, titles.Length > 0, footers.Length > 0, close);
    }

    private static Rect[] Matches(ImageRegion image, IOcrService ocr, string expected, Rect roi, double scale)
    {
        using var pixels = new Mat(image.SrcMat, roi);
        return ocr.OcrResult(pixels).Regions.Where(region => Normalize(region.Text) == Normalize(expected) &&
                float.IsFinite(region.Score) && region.Score > 0 && region.Score <= 1)
            .Select(region => Bounds(region.Rect, roi))
            .Where(bounds => bounds is { } value && Inside(roi, value) &&
                value.Width >= Math.Max(6, 12 * scale) && value.Height >= Math.Max(2, 8 * scale))
            .Select(bounds => bounds!.Value).ToArray();
    }

    private static Rect? Bounds(RotatedRect rectangle, Rect roi)
    {
        if (!float.IsFinite(rectangle.Center.X) || !float.IsFinite(rectangle.Center.Y) ||
            !float.IsFinite(rectangle.Size.Width) || !float.IsFinite(rectangle.Size.Height) ||
            !float.IsFinite(rectangle.Angle) || rectangle.Size.Width <= 0 || rectangle.Size.Height <= 0) return null;
        var points = rectangle.Points();
        var left = Math.Floor(points.Min(point => point.X));
        var top = Math.Floor(points.Min(point => point.Y));
        var right = Math.Ceiling(points.Max(point => point.X));
        var bottom = Math.Ceiling(points.Max(point => point.Y));
        // Reject before integer conversion; OCR coordinates are relative to this ROI.
        if (left < 0 || top < 0 || right > roi.Width || bottom > roi.Height) return null;
        return new Rect(roi.X + (int)left, roi.Y + (int)top, (int)(right - left), (int)(bottom - top));
    }

    private static Rect Scaled(int x, int y, int width, int height, double scale) =>
        new((int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale));
    private static bool Inside(Rect outer, Rect inner) => inner.Width > 0 && inner.Height > 0 &&
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
    private static string Normalize(string? text) => text == null ? "" : string.Concat(text.Where(character => !char.IsWhiteSpace(character)));
}
