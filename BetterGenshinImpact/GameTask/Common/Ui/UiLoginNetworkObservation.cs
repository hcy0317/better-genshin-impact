using System;
using System.Linq;
using System.Text.RegularExpressions;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal enum LoginNetworkKind { None, BlockedModal, Error4201, CleanEntry, AwaitingFeedback }
internal readonly record struct LoginNetworkObservation(CaptureFrameStamp Source, LoginNetworkKind Kind, Rect ButtonBounds)
{
    internal bool IsCandidate => Kind != LoginNetworkKind.None;
    internal bool CanConfirm => Kind == LoginNetworkKind.Error4201 && Source.IsKnown && ButtonBounds is { Width: > 0, Height: > 0 };
    internal bool CanEnter => Kind == LoginNetworkKind.CleanEntry && Source.IsKnown && ButtonBounds is { Width: > 0, Height: > 0 };
    internal bool IsFor(CaptureFrameStamp source) => Source.IsKnown && Source == source;
}

internal static class UiLoginNetworkObservation
{
    internal static LoginNetworkObservation Read(ImageRegion image, Func<IOcrService> ocr)
    {
        var none = new LoginNetworkObservation(image.FrameStamp, LoginNetworkKind.None, default);
        if (image.SrcMat.Empty() || image.Width * 9 != image.Height * 16 ||
            image.SrcMat.Type() != MatType.CV_8UC3 && image.SrcMat.Type() != MatType.CV_8UC4) return none;
        var roi = new Rect(image.Width / 4, image.Height / 5, image.Width / 2, image.Height * 3 / 5);
        if (!HasBoundedModal(image, roi)) return none;
        var blocked = none with { Kind = LoginNetworkKind.BlockedModal };
        using var pixels = new Mat(image.SrcMat, roi);
        var regions = ocr().OcrResult(pixels).Regions;
        var valid = regions.Where(value => float.IsFinite(value.Score) && value.Score > 0 && value.Score <= 1)
            .Select(value => (Text: Normalize(value.Text), Bounds: Bounds(value.Rect, roi)))
            .Where(value => value.Bounds != null).Select(value => (value.Text, Bounds: value.Bounds!.Value)).ToArray();
        var text = string.Join("", valid.Select(value => value.Text));
        if (!text.Contains("网络", StringComparison.Ordinal) && !text.Contains("4201", StringComparison.Ordinal)) return none;
        if (new[] { "验证码", "密码", "人机", "扫码", "其他设备", "账号", "重新登录", "身份验证" }.Any(word => text.Contains(word, StringComparison.Ordinal))) return blocked;
        var titles = valid.Where(value => value.Text == "网络错误" && value.Bounds.Bottom < roi.Y + roi.Height / 3).ToArray();
        var codes = Regex.Matches(text, @"错误(?:代)?码[:：]?(?<code>\d+)");
        var buttons = valid.Where(value => value.Text == "确认" && value.Bounds.Top > roi.Y + roi.Height * 2 / 3 &&
            Math.Abs(value.Bounds.X + value.Bounds.Width / 2d - image.Width / 2d) < image.Width / 10d).ToArray();
        if (titles.Length != 1 || codes.Count != 1 || codes[0].Groups["code"].Value != "4201" ||
            !text.Contains("无法连接网络", StringComparison.Ordinal) || !text.Contains("请检查网络设置", StringComparison.Ordinal) ||
            buttons.Length != 1) return blocked;
        // Keep the existing white-confirm template and threshold; OCR alone is not a button.
        using var white = image.Find(ElementRecognition.Get("BtnWhiteConfirm", image));
        var button = buttons[0].Bounds;
        if (!white.IsExist() || white.X < roi.X || white.Y < roi.Y + roi.Height * 2 / 3 ||
            white.X + white.Width > button.Left || button.Left - (white.X + white.Width) > roi.Width / 3 ||
            Math.Abs(white.Y + white.Height / 2d - (button.Y + button.Height / 2d)) > Math.Max(white.Height, button.Height)) return blocked;
        return new(image.FrameStamp, LoginNetworkKind.Error4201, button);
    }

    internal static LoginNetworkObservation ReadEntry(ImageRegion image, Func<IOcrService> ocr, bool requireEntryMarker = false)
    {
        var none = new LoginNetworkObservation(image.FrameStamp, LoginNetworkKind.None, default);
        var blocked = none with { Kind = LoginNetworkKind.BlockedModal };
        if (image.SrcMat.Empty() || image.Width * 9 != image.Height * 16 ||
            image.SrcMat.Type() != MatType.CV_8UC3 && image.SrcMat.Type() != MatType.CV_8UC4) return requireEntryMarker ? none : blocked;
        using var entry = image.Find(RecognitionAssets.Get("GameLoading", "EnterGame", image));
        if (requireEntryMarker && !entry.IsExist()) return none;
        var center = new Rect(image.Width / 5, image.Height * 15 / 100, image.Width * 3 / 5, image.Height * 65 / 100);
        if (HasBoundedModal(image, center, entryVeto: true)) return blocked;
        using var centralPixels = new Mat(image.SrcMat, center);
        // A clean login gate has no central dialog text. Any detected central text,
        // including an unrecognized authentication prompt, revokes the old eligibility.
        var centralText = ocr().OcrResult(centralPixels).Regions.Select(value => Normalize(value.Text)).Where(value => value.Length != 0).ToArray();
        if (centralText.Any(value => new[] { "验证码", "验证", "密码", "登录", "账号", "扫码", "其他设备", "确认" }
            .Any(word => value.Contains(word, StringComparison.Ordinal)))) return blocked;
        if (!entry.IsExist()) return none;
        if (centralText.Length != 0) return blocked;
        var bounds = new Rect(entry.X, entry.Y, entry.Width, entry.Height);
        if (bounds.Width <= 0 || bounds.Height <= 0 || bounds.X < image.Width / 3 || bounds.Right > image.Width * 2 / 3 ||
            bounds.Top < image.Height * 4 / 5 || bounds.Bottom > image.Height) return blocked;
        using var pixels = new Mat(image.SrcMat, bounds);
        return Normalize(ocr().OcrWithoutDetector(pixels)) == "点击进入"
            ? new(image.FrameStamp, LoginNetworkKind.CleanEntry, bounds) : blocked;
    }

    private static bool HasBoundedModal(ImageRegion image, Rect roi, bool entryVeto = false)
    {
        using var pixels = new Mat(image.SrcMat, roi);
        using var bgr = new Mat();
        if (pixels.Channels() == 4) Cv2.CvtColor(pixels, bgr, ColorConversionCodes.BGRA2BGR); else pixels.CopyTo(bgr);
        using var mask = new Mat();
        Cv2.InRange(bgr, new Scalar(35, 20, 15), new Scalar(110, 100, 90), mask);
        if (entryVeto)
        {
            using var light = new Mat();
            Cv2.InRange(bgr, new Scalar(185, 185, 185), new Scalar(255, 255, 255), light);
            Cv2.BitwiseOr(mask, light, mask);
        }
        using var labels = new Mat(); using var stats = new Mat(); using var centers = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centers);
        var candidates = 0;
        for (var i = 1; i < count; i++)
        {
            var x = stats.At<int>(i, 0); var y = stats.At<int>(i, 1);
            var width = stats.At<int>(i, 2); var height = stats.At<int>(i, 3); var area = stats.At<int>(i, 4);
            if (x > 1 && y > 1 && x + width < roi.Width - 1 && y + height < roi.Height - 1 &&
                width > roi.Width * (entryVeto ? .25 : .7) && height > roi.Height * (entryVeto ? .2 : .55) &&
                area > width * height * (entryVeto ? .8 : .6)) candidates++;
        }
        return entryVeto ? candidates > 0 : candidates == 1;
    }

    private static string Normalize(string value) => string.Concat((value ?? "").Where(character => !char.IsWhiteSpace(character)));
    private static Rect? Bounds(RotatedRect rectangle, Rect roi)
    {
        if (!float.IsFinite(rectangle.Center.X) || !float.IsFinite(rectangle.Center.Y) ||
            !float.IsFinite(rectangle.Size.Width) || !float.IsFinite(rectangle.Size.Height) || !float.IsFinite(rectangle.Angle) ||
            rectangle.Size.Width <= 0 || rectangle.Size.Height <= 0) return null;
        var points = rectangle.Points();
        var left = Math.Floor(points.Min(point => point.X)); var top = Math.Floor(points.Min(point => point.Y));
        var right = Math.Ceiling(points.Max(point => point.X)); var bottom = Math.Ceiling(points.Max(point => point.Y));
        if (left < 0 || top < 0 || right > roi.Width || bottom > roi.Height || right <= left || bottom <= top) return null;
        return new Rect(roi.X + (int)left, roi.Y + (int)top, (int)(right - left), (int)(bottom - top));
    }
}
