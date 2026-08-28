using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static class ArtifactObtainedOrderDetector
{
    internal static Point ToggleCenter(Size captureSize) =>
        ArtifactUiCoordinateMapper.ToCapturePoint(captureSize, 1030, 115);

    internal static bool IsEnabled(Mat capture)
    {
        if (capture.Empty() || capture.Channels() < 3) return false;

        using var toggle = ArtifactUiCoordinateMapper.CropNormalized(
            capture, 990, 92, 75, 48);
        using var bgr = toggle.Channels() == 4
            ? toggle.CvtColor(ColorConversionCodes.BGRA2BGR)
            : toggle.Clone();
        using var white = new Mat();
        Cv2.InRange(
            bgr,
            new Scalar(190, 190, 190),
            new Scalar(255, 255, 255),
            white);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(
            white, labels, stats, centroids,
            PixelConnectivity.Connectivity8, MatType.CV_32S);
        for (var label = 1; label < count; label++)
        {
            var width = stats.At<int>(label, 2);
            var height = stats.At<int>(label, 3);
            var area = stats.At<int>(label, 4);
            if (area < 60 || width is < 12 or > 32 || height is < 12 or > 32) continue;
            return centroids.At<double>(label, 0) > toggle.Width / 2.0;
        }

        using var track = bgr.SubMat(new Rect(14, 14, 24, 20));
        using var gold = new Mat();
        Cv2.InRange(
            track,
            new Scalar(100, 150, 180),
            new Scalar(180, 220, 245),
            gold);
        return Cv2.CountNonZero(gold) >= 24 * 20 * 0.15;
    }

    internal static IReadOnlyList<bool> RequiredStates(bool initiallyEnabled) =>
        initiallyEnabled ? [false, true] : [true];

    internal static async Task ResetAndEnsureEnabledAsync(
        CancellationToken cancellationToken)
    {
        bool initiallyEnabled;
        using (var initialCapture = CaptureToRectArea())
        {
            initiallyEnabled = IsEnabled(initialCapture.SrcMat);
        }

        foreach (var targetState in RequiredStates(initiallyEnabled))
        {
            using var capture = CaptureToRectArea();
            var center = ToggleCenter(capture.SrcMat.Size());
            capture.ClickTo(center.X, center.Y);
            await WaitForStateAsync(targetState, cancellationToken);
        }
    }

    private static async Task WaitForStateAsync(
        bool expectedEnabled,
        CancellationToken cancellationToken)
    {
        var stableFrames = 0;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var capture = CaptureToRectArea();
            stableFrames = IsEnabled(capture.SrcMat) == expectedEnabled
                ? stableFrames + 1
                : 0;
            if (stableFrames >= 2) return;
            await Delay(80, cancellationToken);
        }
        throw new InvalidDataException(
            $"按获得时间顺序开关未能稳定切换为 {(expectedEnabled ? "开启" : "关闭")} 状态");
    }

}
