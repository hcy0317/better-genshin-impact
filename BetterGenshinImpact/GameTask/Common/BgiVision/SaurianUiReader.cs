using System;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.BgiVision;

/// <summary>已取证变身HUD的物理宏准入，不代表任何角色技能或任务成功。</summary>
internal static class SaurianUiReader
{
    internal static bool IsKnownTransformation(ImageRegion frame) =>
        frame.ReadOnce(typeof(SaurianUiReader), () => Read(frame));

    private static bool Read(ImageRegion frame)
    {
        var pixels = frame.SrcMat;
        if (pixels.Empty() || frame.Width * 9 != frame.Height * 16 ||
            pixels.Type() != MatType.CV_8UC3 && pixels.Type() != MatType.CV_8UC4) return false;
        // 只读取小ROI；导航热路径不能为了HUD判定复制两份整帧。
        var scale = frame.Width / 1920d;
        using var bar = new Mat(pixels, Scaled(new Rect(810, 1007, 280, 7), scale));
        using var orange = new Mat();
        Cv2.InRange(bar, new Scalar(40, 194, 245, 0), new Scalar(60, 214, 255, 255), orange);
        if (Cv2.CountNonZero(orange) < bar.Width * bar.Height * .28) return false;
        if (!BrightAnchor(frame, "PaimonMenu", 235) && !BrightAnchor(frame, "FriendChat", 220)) return false;
        // 只匹配退出图标本体，避免灰态图标周围的大块世界背景主导相关度。
        return Match(pixels, new Rect(1803, 974, 33, 43), scale, SaurianUiTemplates.Exit, binary: false) >= .85 &&
            Match(pixels, new Rect(1275, 970, 75, 55), scale, SaurianUiTemplates.Aim, binary: true) >= .88;
    }

    private static bool BrightAnchor(ImageRegion frame, string name, int minimum)
    {
        using var anchor = frame.Find(ElementRecognition.Get(name, frame));
        if (!anchor.IsExist()) return false;
        using var area = frame.DeriveCrop(new Rect(anchor.X, anchor.Y, anchor.Width, anchor.Height));
        using var white = new Mat();
        Cv2.InRange(area.SrcMat, new Scalar(minimum, minimum, minimum, 0), Scalar.All(255), white);
        return Cv2.CountNonZero(white) >= Math.Max(4, anchor.Width * anchor.Height / 20);
    }

    private static Rect Scaled(Rect rect, double scale) => new((int)Math.Round(rect.X * scale),
        (int)Math.Round(rect.Y * scale), Math.Max(1, (int)Math.Round(rect.Width * scale)), Math.Max(1, (int)Math.Round(rect.Height * scale)));

    private static double Match(Mat frame, Rect rect, double scale, string encoded, bool binary)
    {
        using var reference = Cv2.ImDecode(Convert.FromBase64String(encoded), ImreadModes.Grayscale);
        using var nativeCrop = new Mat(frame, Scaled(new Rect(rect.X - 4, rect.Y - 4, rect.Width + 8, rect.Height + 8), scale));
        using var crop = new Mat();
        Cv2.Resize(nativeCrop, crop, new Size(rect.Width + 8, rect.Height + 8));
        using var source = new Mat();
        if (binary) Cv2.InRange(crop, new Scalar(220, 220, 220, 0), Scalar.All(255), source);
        else Cv2.CvtColor(crop, source, crop.Channels() == 4 ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        using var result = new Mat();
        Cv2.MatchTemplate(source, reference, result, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(result, out double _, out double score);
        return double.IsFinite(score) ? score : 0;
    }
}
