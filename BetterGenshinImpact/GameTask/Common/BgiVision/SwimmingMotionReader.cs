using System;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.BgiVision;

/// <summary>复用既有游泳鼠标提示的黄色连通域；不初始化OCR、角色或应用宿主。</summary>
internal static class SwimmingMotionReader
{
    internal static bool IsSwimming(ImageRegion frame) => frame.ReadOnce(typeof(SwimmingMotionReader), () => Read(frame));

    private static bool Read(ImageRegion frame)
    {
        if (frame.SrcMat.Empty() || frame.SrcMat.Channels() is not (3 or 4) ||
            frame.Width < 640 || frame.Width * 9 != frame.Height * 16) return false;
        var scale = frame.Width / 1920d;
        using var crop = frame.DeriveCrop(1819 * scale, 1025 * scale, 9 * scale, 11 * scale);
        using var mask = OpenCvCommonHelper.Threshold(crop.SrcMat,
            new Scalar(242, 223, 39), new Scalar(255, 233, 44));
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            PixelConnectivity.Connectivity4, MatType.CV_32S);
        var minimum = Math.Max(2, (int)Math.Ceiling(4 * scale * scale));
        for (var index = 1; index < count; index++)
            if (stats.At<int>(index, 4) >= minimum) return true;
        return false;
    }
}
