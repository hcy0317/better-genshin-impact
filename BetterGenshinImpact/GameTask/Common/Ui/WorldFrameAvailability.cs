using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using System;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.GameTask.Common.Ui;

internal enum WorldFrameKind { Unknown, Playable, Reconnecting, Loading }

internal static class WorldFrameAvailability
{
    internal static WorldFrameKind ReadNative(ImageRegion image) => ReadWorld(image,
        SaurianUiReader.IsKnownTransformation, Bv.IsCombatHud, Bv.IsInBigMapUi, OcrFactory.Paddle);

    internal static WorldFrameKind ReadWorld(ImageRegion image, Func<ImageRegion, bool> transformed,
        Func<ImageRegion, bool> ordinaryHud, Func<ImageRegion, bool> bigMap, IOcrService ocr) =>
        image.ReadOnce(typeof(WorldFrameAvailability), () =>
            // Both readers positively confirm unobscured world anchors. A transformed avatar has no ordinary HP bar.
            Read(image, (transformed(image) || ordinaryHud(image)) && !bigMap(image), ocr));

    internal static WorldFrameKind Read(ImageRegion image, bool playableHud, IOcrService ocr)
    {
        if (image.SrcMat.Empty() || image.Width * 9 != image.Height * 16 ||
            image.SrcMat.Type() != MatType.CV_8UC3 && image.SrcMat.Type() != MatType.CV_8UC4)
            return WorldFrameKind.Unknown;
        if (HasReconnectPanel(image))
        {
            var bounds = new Rect(image.Width * 39 / 100, image.Height * 47 / 100,
                image.Width * 22 / 100, image.Height * 6 / 100);
            using var textArea = new Mat(image.SrcMat, bounds);
            var text = ocr.OcrWithoutDetector(textArea).Replace(" ", "", StringComparison.Ordinal);
            if (text.Contains("重新连接服务器", StringComparison.Ordinal) ||
                text.Contains("重新連接伺服器", StringComparison.Ordinal) ||
                text.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase))
                return WorldFrameKind.Reconnecting;
            // 形状像阻断弹窗但文字未确认，不能因后方HUD可见就授予移动。
            return WorldFrameKind.Unknown;
        }
        if (playableHud) return WorldFrameKind.Playable;
        using var center = new Mat(image.SrcMat, new Rect(image.Width / 4, image.Height / 4,
            image.Width / 2, image.Height / 2));
        using var gray = new Mat();
        Cv2.CvtColor(center, gray, image.SrcMat.Channels() == 4
            ? ColorConversionCodes.BGRA2GRAY : ColorConversionCodes.BGR2GRAY);
        Cv2.MeanStdDev(gray, out Scalar mean, out Scalar deviation);
        return (mean.Val0 < 10 || mean.Val0 > 240) && deviation.Val0 < 12
            ? WorldFrameKind.Loading : WorldFrameKind.Unknown;
    }

    private static bool HasReconnectPanel(ImageRegion image)
    {
        Vec3b? first = null;
        foreach (var (x, y) in new[] { (36, 48), (64, 48), (36, 52), (64, 52) })
        {
            var px = image.Width * x / 100;
            var py = image.Height * y / 100;
            var value = image.SrcMat.Channels() == 3 ? image.SrcMat.At<Vec3b>(py, px)
                : new Vec3b(image.SrcMat.At<Vec4b>(py, px).Item0,
                    image.SrcMat.At<Vec4b>(py, px).Item1, image.SrcMat.At<Vec4b>(py, px).Item2);
            if (value.Item0 is < 35 or > 110 || value.Item1 is < 20 or > 90 ||
                value.Item2 is < 15 or > 80 || value.Item0 - value.Item2 < 10) return false;
            if (first is { } reference && (Math.Abs(value.Item0 - reference.Item0) > 8 ||
                Math.Abs(value.Item1 - reference.Item1) > 8 || Math.Abs(value.Item2 - reference.Item2) > 8)) return false;
            first = value;
        }
        // 岩壁等大块暗蓝背景也能通过四点采样。必须再有独立的、有界矩形
        // 面板证据，才能让OCR失败否决正常HUD；不能把均匀世界背景当弹窗。
        using var center = new Mat(image.SrcMat, new Rect(image.Width * 30 / 100, image.Height * 40 / 100,
            image.Width * 40 / 100, image.Height * 20 / 100));
        using var mask = new Mat();
        var color = first!.Value;
        Cv2.InRange(center, new Scalar(color.Item0 - 8, color.Item1 - 8, color.Item2 - 8, 0),
            new Scalar(color.Item0 + 8, color.Item1 + 8, color.Item2 + 8, 255), mask);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            PixelConnectivity.Connectivity4, MatType.CV_32S);
        for (var i = 1; i < count; i++)
        {
            var x = stats.At<int>(i, 0);
            var y = stats.At<int>(i, 1);
            var width = stats.At<int>(i, 2);
            var height = stats.At<int>(i, 3);
            if (x <= 0 || y <= 0 || x + width >= center.Width || y + height >= center.Height) continue;
            if (width < image.Width * .28 || width > image.Width * .36 ||
                height < image.Height * .06 || height > image.Height * .12) continue;
            if (Math.Abs(x + width / 2d - center.Width / 2d) > image.Width * .02 ||
                Math.Abs(y + height / 2d - center.Height / 2d) > image.Height * .02) continue;
            if (stats.At<int>(i, 4) >= width * height * .75) return true;
        }
        return false;
    }
}
