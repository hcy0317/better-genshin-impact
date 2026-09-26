using System;
using System.Threading;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal static class MapContentReadiness
{
    internal static bool IsReady(bool mapReady, Mat grey) => mapReady && !IsBlank(grey);
    // 只否决已观察到的深色、近乎均匀的空白底图；亮色海面/低纹理地图不被当成加载屏。
    // 通过此检查不代表已定位，调用方仍需地图控件、来源与原有特征匹配证据。
    internal static bool IsBlank(Mat grey)
    {
        if (grey.Empty() || grey.Width < 16 || grey.Height < 16) return true;
        using var center = new Mat(grey, new Rect(grey.Width / 5, grey.Height / 5,
            grey.Width * 3 / 5, grey.Height * 3 / 5));
        Cv2.MeanStdDev(center, out Scalar mean, out Scalar deviation);
        Cv2.MinMaxLoc(center, out double min, out double max);
        return mean.Val0 <= 32 && deviation.Val0 <= 3 && max - min <= 16;
    }

    internal static T Match<T>(ImageRegion frame, Func<Mat, T> recognize, CancellationToken ct,
        string phase, ILogger? logger = null)
    {
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        if (IsBlank(frame.CacheGreyMat))
        {
            Record(frame, phase + "-blank", "地图控件存在但内容为空白，未调用特征匹配", logger);
            UiOperation.Current?.Observe("地图内容加载后定位", $"phase={phase},content=blank", frame.FrameStamp.Sequence);
            throw new MapPositionNotRecognizedException("地图内容尚未加载，禁止按预设坐标定位或拖动");
        }
        var result = recognize(frame.CacheGreyMat);
        ct.ThrowIfCancellationRequested();
        UiOperation.Current?.Check();
        return result;
    }

    internal static void Record(ImageRegion frame, string phase, string detail, ILogger? logger)
    {
        try
        {
            if (UiOperation.Current is not { } operation) return;
            DiagnosticEvidenceScope.Current?.TryCapture(frame, "map:" + operation.Id, phase,
                $"root={operation.RootId}; op={operation.Id}; {detail}", logger);
        }
        catch { /* 取证失败不改变输入或识别结果。 */ }
    }
}
