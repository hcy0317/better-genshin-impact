using OpenCvSharp;
using BetterGenshinImpact.GameTask.Model.GameUI;
using System;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static class ArtifactRarityDetector
{
    private static readonly Vec3b[] YasPanelColors =
    [
        new Vec3b(139, 119, 113),
        new Vec3b(114, 143, 42),
        new Vec3b(203, 127, 81),
        new Vec3b(224, 86, 161),
        new Vec3b(50, 105, 188)
    ];

    internal static int Detect(Mat capture)
    {
        var position = SamplePosition(capture.Size());
        var color = capture.Channels() switch
        {
            4 => ToBgr(capture.At<Vec4b>(position.Y, position.X)),
            3 => capture.At<Vec3b>(position.Y, position.X),
            var channels => throw new InvalidOperationException(
                $"不支持的圣遗物星级截图通道数：{channels}")
        };
        return DetectColor(color);
    }

    internal static Point SamplePosition(Size captureSize)
    {
        return ArtifactUiCoordinateMapper.ToCapturePoint(
            captureSize, 1469.4, 123.9);
    }

    internal static int DetectColor(Vec3b color)
    {
        var closestRarity = 1;
        var closestDistance = long.MaxValue;
        for (var index = 0; index < YasPanelColors.Length; index++)
        {
            var candidate = YasPanelColors[index];
            var blue = color.Item0 - candidate.Item0;
            var green = color.Item1 - candidate.Item1;
            var red = color.Item2 - candidate.Item2;
            var distance = (long)blue * blue +
                           (long)green * green +
                           (long)red * red;
            if (distance < closestDistance)
            {
                closestDistance = distance;
                closestRarity = index + 1;
            }
        }

        return closestRarity;
    }

    private static Vec3b ToBgr(Vec4b color)
    {
        return new Vec3b(color.Item0, color.Item1, color.Item2);
    }
}
