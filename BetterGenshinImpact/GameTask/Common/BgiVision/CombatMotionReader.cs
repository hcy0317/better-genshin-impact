using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using OpenCvSharp;
using System;
using BetterGenshinImpact.Core.Recognition;
using System.Linq;

namespace BetterGenshinImpact.GameTask.Common.BgiVision;

internal readonly record struct CombatControlObservation
{
    private readonly MotionStatus _motion;
    internal bool IsObserved { get; }
    internal MotionStatus Motion => IsObserved ? _motion : MotionStatus.Unknown;
    internal bool KeyboardBreakoutRequested { get; }
    internal CombatControlObservation(MotionStatus motion, bool keyboardBreakoutRequested)
    { _motion = motion; KeyboardBreakoutRequested = keyboardBreakoutRequested; IsObserved = true; }
}

/// <summary>战斗姿态必须有正证据；不改变非战斗寻路的旧兼容入口。</summary>
internal static class CombatMotionReader
{
    internal static MotionStatus Read(ImageRegion frame, bool combatHud, IOcrService ocr)
        => ReadControl(frame, combatHud, ocr).Motion;

    internal static CombatControlObservation ReadControl(ImageRegion frame, bool combatHud, IOcrService ocr)
    {
        if (!combatHud || frame.SrcMat.Empty()) return default;
        return frame.ReadOnce(typeof(CombatMotionReader), () =>
        {
            using var realtime = new RecognitionReadinessScope();
            try { return ReadFrame(frame, ocr); }
            catch (RecognitionNotReadyException) { return default; }
        });
    }

    private static CombatControlObservation ReadFrame(ImageRegion frame, IOcrService ocr)
    {
        // 挣脱提示位于画面中右，和右下角飞行/攀爬提示是不同证据区域。
        if (HasKeyboardBreakoutPrompt(frame, ocr)) return new(MotionStatus.Unknown, true);
        using var space = frame.Find(ElementRecognition.Get("SpaceKey", frame));
        using var drop = frame.Find(ElementRecognition.Get("XKey", frame));
        if (space.IsExist()) return new(drop.IsExist() ? MotionStatus.Climb : MotionStatus.Fly, false);
        // 技能标签在冻结时仍显示，不能据此证明正常/可移动。技能就绪由独立E/Q观察负责。
        return new(MotionStatus.Unknown, false);
    }

    private static bool HasKeyboardBreakoutPrompt(ImageRegion frame, IOcrService ocr)
    {
        var scale = frame.Width / 1920d;
        var roi = new Rect((int)(frame.Width * .67), (int)(frame.Height * .40),
            (int)(frame.Width * .15), (int)(frame.Height * .24));
        using var region = frame.DeriveCrop(roi);
        using var gray = new Mat();
        if (region.SrcMat.Channels() == 1) region.SrcMat.CopyTo(gray);
        else Cv2.CvtColor(region.SrcMat, gray, ColorConversionCodes.BGR2GRAY);
        using var mask = new Mat();
        Cv2.Threshold(gray, mask, 230, 255, ThresholdTypes.Binary);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(3, 3));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
        var candidates = contours.Select(contour => (Rect: Cv2.BoundingRect(contour), Area: Cv2.ContourArea(contour)))
            .Where(item => item.Rect.Width >= 50 * scale && item.Rect.Width <= 130 * scale &&
                item.Rect.Height >= 18 * scale && item.Rect.Height <= 44 * scale &&
                item.Rect.Width / (double)item.Rect.Height is >= 1.8 and <= 5 &&
                item.Area >= item.Rect.Width * item.Rect.Height * .6)
            .OrderByDescending(item => item.Area).Take(3);
        foreach (var candidate in candidates)
        {
            using var key = region.DeriveCrop(candidate.Rect);
            if (ocr.OcrWithoutDetector(key.SrcMat).Trim().Equals("Space", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
