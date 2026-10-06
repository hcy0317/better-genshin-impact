using System;
using System.Collections.Generic;
using System.Linq;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal static class MapTargetClickAdmission
{
    // 坐标只读；Force也不能把附近图标吸附到原请求上。
    internal static bool Accepts(bool force, double x, double y, Rect? matchedTarget,
        IReadOnlyCollection<Rect> compatibleIcons)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        bool Contains(Rect rect) => rect.Width > 0 && rect.Height > 0 &&
            x >= rect.X && x < rect.Right && y >= rect.Y && y < rect.Bottom;
        return force ? compatibleIcons.Any(Contains) : matchedTarget is { } target && Contains(target);
    }
}
