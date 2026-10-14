using System;
using System.Linq;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal enum DialogueOptions { Unknown, Absent, Present }
internal enum DialogueEvidenceState { Unknown, Confirmed, Rejected }
internal readonly record struct DialogueObservation(CaptureFrameStamp Source, DialogueEvidenceState EvidenceState, Rect? ExitBounds,
    DialogueOptions Options, string? ContentFingerprint, bool AdvancePromptConfirmed)
{
    internal bool Confirmed => EvidenceState == DialogueEvidenceState.Confirmed;
    internal string? StageFingerprint { get; init; }
    internal bool IsFor(CaptureFrameStamp source) => Confirmed && source.IsKnown && source == Source;
    internal bool CanAdvance => Options == DialogueOptions.Absent && AdvancePromptConfirmed && !string.IsNullOrEmpty(ContentFingerprint);
    internal bool SamePage(DialogueObservation other) => Confirmed && other.Confirmed &&
        ContentFingerprint != null && ContentFingerprint == other.ContentFingerprint && Options == other.Options && StageFingerprint == other.StageFingerprint;
}

internal static class DialogueUiReader
{
    internal static DialogueObservation Read(ImageRegion image, IOcrService ocr)
    {
        if (!image.FrameStamp.IsKnown || image.Width * 9 != image.Height * 16 || image.Width < 320)
            return new(image.FrameStamp, DialogueEvidenceState.Rejected, null, DialogueOptions.Unknown, null, false);
        using var exit = image.Find(RecognitionAssets.Get("AutoSkip", "ChatExit", image));
        var hasOptions = false;
        foreach (var name in new[] { "OptionIcon", "DailyRewardIcon", "ExploreIcon", "ExclamationIcon" })
        {
            var matches = image.FindMulti(RecognitionAssets.Get("AutoSkip", name, image));
            try { hasOptions |= matches.Count > 0; }
            finally { foreach (var match in matches) match.Dispose(); }
        }
        // A missed icon must not turn visible option text into "no options".
        using var options = new Mat(image.SrcMat, new Rect(image.Width / 2, image.Height / 6,
            image.Width / 3, image.Height * 3 / 5));
        var optionText = ocr.Ocr(options);
        using var body = new Mat(image.SrcMat, new Rect(image.Width / 5, image.Height * 84 / 100,
            image.Width * 3 / 5, image.Height * 9 / 100));
        var text = ocr.Ocr(body);
        using var speaker = new Mat(image.SrcMat, new Rect(image.Width / 3, image.Height * 78 / 100,
            image.Width / 3, image.Height * 6 / 100));
        var speakerText = ocr.Ocr(speaker);
        var normalized = new string(text.Where(c => !char.IsWhiteSpace(c)).Take(1024).ToArray());
        var optionState = hasOptions || !string.IsNullOrWhiteSpace(optionText) ? DialogueOptions.Present : DialogueOptions.Absent;
        Rect? exitBounds = exit.IsExist() ? new(exit.X, exit.Y, exit.Width, exit.Height) : null;
        var stageText = new string((speakerText + optionText).Where(c => !char.IsWhiteSpace(c)).Take(1024).ToArray());
        return new(image.FrameStamp, DialogueEvidenceState.Confirmed, exitBounds, optionState,
            normalized.Length >= 2 ? Fingerprint(normalized) : null, HasAdvanceMarker(image))
        { StageFingerprint = stageText.Length > 0 || exitBounds != null ? Fingerprint(stageText + "|exit=" + (exitBounds != null)) : null };
    }

    private static string Fingerprint(string text)
    {
        ulong value = 14695981039346656037;
        foreach (var c in text) value = unchecked((value ^ c) * 1099511628211);
        return value.ToString("X16");
    }

    private static bool HasAdvanceMarker(ImageRegion image)
    {
        using var area = new Mat(image.SrcMat, new Rect(image.Width * 48 / 100, image.Height * 94 / 100,
            image.Width * 4 / 100, image.Height * 5 / 100));
        using var bgr = new Mat();
        if (area.Channels() == 4) Cv2.CvtColor(area, bgr, ColorConversionCodes.BGRA2BGR); else area.CopyTo(bgr);
        using var hsv = new Mat();
        using var mask = new Mat();
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.InRange(hsv, new Scalar(15, 100, 100), new Scalar(40, 255, 255), mask);
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var scale = image.Height / 1080d;
        return contours.Count(contour =>
        {
            var box = Cv2.BoundingRect(contour);
            var polygon = Cv2.ApproxPolyDP(contour, Cv2.ArcLength(contour, true) * .045, true);
            return polygon.Length == 4 && Cv2.IsContourConvex(polygon) &&
                Cv2.ContourArea(polygon) / Math.Max(1, box.Width * box.Height) is > .32 and < .68 &&
                box.Width >= 16 * scale && box.Width <= 38 * scale && box.Height >= 16 * scale && box.Height <= 38 * scale &&
                box.Width / (double)box.Height is > .65 and < 1.4 &&
                Math.Abs(box.X + box.Width / 2d - area.Width / 2d) <= 5 * scale;
        }) == 1;
    }
}
