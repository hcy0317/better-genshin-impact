using System;
using System.Globalization;
using System.Linq;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.BgiVision;

internal static class CriticalHealthHud
{
    private readonly record struct Observation(CaptureFrameStamp Source, bool Living);

    // 仅由已经验证双亮HUD锚点且无确认弹窗的检测器调用，不单凭数字授予HUD。
    internal static bool Read(ImageRegion frame, IOcrService ocr) => frame.ReadOnce(typeof(CriticalHealthHud), () =>
    {
        var scale = frame.Height / 1080d;
        using var area = new Mat(frame.SrcMat, new Rect((int)Math.Round(frame.Width / 2d - 90 * scale),
            (int)Math.Round(996 * scale), (int)Math.Round(225 * scale), (int)Math.Round(30 * scale)));
        return new Observation(frame.FrameStamp, IsPositiveCriticalHealth(ocr.OcrWithoutDetector(area)));
    }).Living;

    internal static bool IsKnownLowHp(ImageRegion frame) =>
        frame.TryReadObservation<Observation>(typeof(CriticalHealthHud), out var observed) &&
        observed.Source == frame.FrameStamp && observed.Living;

    internal static bool IsPositiveCriticalHealth(string text)
    {
        var parts = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Split('/');
        if (parts.Length != 2 || parts.Any(part => part.Length is < 1 or > 7 || part.Any(c => c is < '0' or > '9')))
            return false;
        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hp) &&
            int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var maximum) &&
            hp > 0 && maximum > 0 && hp <= maximum * .02;
    }
}
