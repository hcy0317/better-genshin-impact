using OpenCvSharp;
using System;
using System.IO;

namespace BetterGenshinImpact.GameTask.Model.GameUI;

internal static class ArtifactUiCoordinateMapper
{
    internal static readonly Size LogicalSize = new(1600, 900);

    internal static Point ToCapturePoint(
        Size captureSize,
        double logicalX,
        double logicalY) => new(
        Scale(logicalX, captureSize.Width / (double)LogicalSize.Width),
        Scale(logicalY, captureSize.Height / (double)LogicalSize.Height));

    internal static Rect ToCaptureRect(
        Size captureSize,
        double logicalX,
        double logicalY,
        double logicalWidth,
        double logicalHeight)
    {
        var left = Scale(logicalX, captureSize.Width / (double)LogicalSize.Width);
        var top = Scale(logicalY, captureSize.Height / (double)LogicalSize.Height);
        var width = Math.Max(1, Scale(logicalWidth, captureSize.Width / (double)LogicalSize.Width));
        var height = Math.Max(1, Scale(logicalHeight, captureSize.Height / (double)LogicalSize.Height));
        left = Math.Clamp(left, 0, captureSize.Width - 1);
        top = Math.Clamp(top, 0, captureSize.Height - 1);
        return new Rect(
            left,
            top,
            Math.Min(width, captureSize.Width - left),
            Math.Min(height, captureSize.Height - top));
    }

    internal static Mat CropNormalized(
        Mat capture,
        double logicalX,
        double logicalY,
        double logicalWidth,
        double logicalHeight)
    {
        if (capture.Empty()) throw new InvalidDataException("圣遗物界面截图为空");
        var sourceRect = ToCaptureRect(
            capture.Size(), logicalX, logicalY, logicalWidth, logicalHeight);
        using var source = capture.SubMat(sourceRect);
        var targetSize = new Size(
            Math.Max(1, Scale(logicalWidth, 1)),
            Math.Max(1, Scale(logicalHeight, 1)));
        if (source.Size() == targetSize) return source.Clone();
        var normalized = new Mat();
        Cv2.Resize(source, normalized, targetSize, 0, 0, InterpolationFlags.Area);
        return normalized;
    }

    internal static int ScaleLogicalX(Size captureSize, double logicalWidth) =>
        Math.Max(1, Scale(logicalWidth, captureSize.Width / (double)LogicalSize.Width));

    internal static int ScaleLogicalY(Size captureSize, double logicalHeight) =>
        Math.Max(1, Scale(logicalHeight, captureSize.Height / (double)LogicalSize.Height));

    private static int Scale(double value, double scale) =>
        (int)Math.Round(value * scale, MidpointRounding.AwayFromZero);
}
