using System;
using System.Linq;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.BgiVision;

internal readonly record struct CannonUiObservation(CaptureFrameStamp Source, int DirectionKeys,
    bool ExitControl, string? Title, string? FirePrompt)
{
    internal bool CanExit => DirectionKeys >= 4 && ExitControl &&
        Title?.Contains("神居岛崩炮", StringComparison.Ordinal) == true;
    internal bool CanFire => CanExit && FirePrompt?.Contains("Enter", StringComparison.OrdinalIgnoreCase) == true &&
        FirePrompt.Contains("发射", StringComparison.Ordinal);
    internal bool IsFor(CaptureFrameStamp source) => Source.IsKnown && Source == source;
}

/// <summary>只识别当前帧的具体大炮交互能力；不点击、不授权普通确认页或战斗技能。</summary>
internal static class CannonUiReader
{
    internal static CannonUiObservation Read(ImageRegion frame, IOcrService ocr) =>
        frame.ReadOnce(typeof(CannonUiReader), () => ReadFrame(frame, ocr));

    private static CannonUiObservation ReadFrame(ImageRegion frame, IOcrService ocr)
    {
        if (frame.SrcMat.Empty() || frame.Width * 9 != frame.Height * 16) return default;
        var scale = frame.Width / 1920d;
        using var direction = frame.DeriveCrop(Scaled(185, 752, 282, 255, scale));
        var keys = WhiteShapes(direction).Count(item =>
            item.Rect.Width >= 25 * scale && item.Rect.Width <= 64 * scale &&
            item.Rect.Height >= 20 * scale && item.Rect.Height <= 54 * scale &&
            item.Rect.Width / (double)item.Rect.Height is >= .75 and <= 1.8 &&
            item.Area >= item.Rect.Width * item.Rect.Height * .72);
        if (keys < 4) return new(frame.FrameStamp, keys, false, null, null);

        using var close = frame.DeriveCrop(Scaled(1785, 0, 110, 105, scale));
        var exit = WhiteShapes(close).Any(item =>
            item.Rect.Width >= 25 * scale && item.Rect.Width <= 60 * scale &&
            item.Rect.Height >= 25 * scale && item.Rect.Height <= 60 * scale &&
            item.Rect.Width / (double)item.Rect.Height is >= .8 and <= 1.25 &&
            item.Area >= item.Rect.Width * item.Rect.Height * .2 &&
            item.Area <= item.Rect.Width * item.Rect.Height * .7);
        if (!exit) return new(frame.FrameStamp, keys, false, null, null);

        using var titleArea = frame.DeriveCrop(Scaled(580, 140, 850, 65, scale));
        var title = Normalize(ocr.OcrWithoutDetector(titleArea.SrcMat));
        if (!title.Contains("神居岛崩炮", StringComparison.Ordinal))
            return new(frame.FrameStamp, keys, exit, title, null);
        using var fireArea = frame.DeriveCrop(Scaled(1660, 988, 230, 55, scale));
        return new(frame.FrameStamp, keys, exit, title, Normalize(ocr.OcrWithoutDetector(fireArea.SrcMat)));
    }

    private static Rect Scaled(int x, int y, int width, int height, double scale) =>
        new((int)(x * scale), (int)(y * scale), (int)(width * scale), (int)(height * scale));

    private static string Normalize(string text) => string.Concat(text.Where(character => !char.IsWhiteSpace(character)));

    private static (Rect Rect, double Area)[] WhiteShapes(ImageRegion frame)
    {
        using var gray = new Mat();
        if (frame.SrcMat.Channels() == 1) frame.SrcMat.CopyTo(gray);
        else Cv2.CvtColor(frame.SrcMat, gray, ColorConversionCodes.BGR2GRAY);
        using var white = new Mat();
        Cv2.Threshold(gray, white, 230, 255, ThresholdTypes.Binary);
        Cv2.FindContours(white, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        return contours.Select(contour => (Cv2.BoundingRect(contour), Cv2.ContourArea(contour))).ToArray();
    }
}
