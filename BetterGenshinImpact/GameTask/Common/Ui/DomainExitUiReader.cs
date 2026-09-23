using System;
using System.Linq;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal readonly record struct DomainExitObservation(CaptureFrameStamp Source, bool Visible, Rect ConfirmBounds)
{
    internal bool IsFor(CaptureFrameStamp source) => Visible && Source.IsKnown && Source == source &&
        ConfirmBounds.Width > 0 && ConfirmBounds.Height > 0;
}

/// <summary>只识别退出秘境的双按钮弹窗，不把通用黑确认按钮当作操作许可。</summary>
internal static class DomainExitUiReader
{
    internal static DomainExitObservation Read(ImageRegion image, IOcrService ocr)
    {
        var empty = new DomainExitObservation(image.FrameStamp, false, default);
        if (image.SrcMat.Empty() || image.Width * 9 != image.Height * 16) return empty;
        var scale = image.Width / 1920d;
        Rect Scale(int x, int y, int w, int h) => new((int)(x * scale), (int)(y * scale),
            Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)));
        var rightButton = Scale(975, 720, 380, 75);
        using var confirm = image.Find(ElementRecognition.Get("BtnBlackConfirm", image));
        var bounds = new Rect(confirm.X, confirm.Y, confirm.Width, confirm.Height);
        if (!confirm.IsExist() || !Inside(rightButton, bounds)) return empty;
        if (!HasText(Scale(850, 270, 220, 65), "提示") ||
            !HasText(Scale(800, 470, 320, 90), "退出秘境") ||
            !HasText(Scale(680, 725, 210, 65), "取消") ||
            !HasText(Scale(1090, 725, 210, 65), "确认")) return empty;
        return new(image.FrameStamp, true, bounds);

        bool HasText(Rect roi, string expected)
        {
            if (!Inside(new Rect(0, 0, image.Width, image.Height), roi)) return false;
            using var pixels = new Mat(image.SrcMat, roi);
            var regions = ocr.OcrResult(pixels).Regions;
            return regions.Count(region =>
            {
                var box = region.Rect;
                if (!float.IsFinite(region.Score) || region.Score <= 0 || region.Score > 1 ||
                    !float.IsFinite(box.Center.X) || !float.IsFinite(box.Center.Y) ||
                    !float.IsFinite(box.Size.Width) || !float.IsFinite(box.Size.Height) ||
                    !float.IsFinite(box.Angle) || box.Size.Width < 12 * scale || box.Size.Height < 8 * scale)
                    return false;
                return string.Concat(region.Text.Where(c => !char.IsWhiteSpace(c))) == expected &&
                    box.Points().All(p => p.X >= 0 && p.Y >= 0 && p.X <= roi.Width && p.Y <= roi.Height);
            }) == 1;
        }
    }

    private static bool Inside(Rect outer, Rect inner) => inner.Width > 0 && inner.Height > 0 &&
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;
}
