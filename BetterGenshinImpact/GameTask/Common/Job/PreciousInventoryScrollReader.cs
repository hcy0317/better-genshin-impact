using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal static class PreciousInventoryScrollReader
{
    private const int TrackTop = 122;
    private const int TrackBottom = 940;

    internal static InventoryScrollObservation Read(Mat frame)
    {
        if (frame.Empty() || frame.Width * 9 != frame.Height * 16 ||
            frame.Type() != MatType.CV_8UC3 && frame.Type() != MatType.CV_8UC4) return default;
        var scale = frame.Width / 1920d;
        using var native = new Mat(frame, new Rect((int)Math.Round(1279 * scale), (int)Math.Round(TrackTop * scale),
            Math.Max(1, (int)Math.Round(20 * scale)), Math.Max(1, (int)Math.Round((TrackBottom - TrackTop) * scale))));
        using var color = new Mat();
        if (native.Channels() == 4) Cv2.CvtColor(native, color, ColorConversionCodes.BGRA2BGR);
        else native.CopyTo(color);
        using var strip = color.Resize(new Size(20, TrackBottom - TrackTop));
        var segments = new List<(int Top, int Bottom)>();
        var start = -1;
        for (var y = 0; y <= strip.Height; y++)
        {
            var thumb = false;
            if (y < strip.Height)
            {
                var bright = 0;
                double center = 0, sides = 0;
                for (var x = 6; x <= 13; x++)
                {
                    var p = strip.At<Vec3b>(y, x);
                    var min = Math.Min(p.Item0, Math.Min(p.Item1, p.Item2));
                    var max = Math.Max(p.Item0, Math.Max(p.Item1, p.Item2));
                    if (min >= 160 && max - min <= 30) bright++;
                }
                for (var x = 8; x <= 10; x++) { var p = strip.At<Vec3b>(y, x); center += (p.Item0 + p.Item1 + p.Item2) / 9d; }
                for (var x = 1; x <= 3; x++)
                {
                    var left = strip.At<Vec3b>(y, x);
                    var right = strip.At<Vec3b>(y, x + 15);
                    sides += (left.Item0 + left.Item1 + left.Item2 + right.Item0 + right.Item1 + right.Item2) / 18d;
                }
                thumb = bright >= 5 && center - sides >= 25;
            }
            if (thumb && start < 0) start = y;
            if (!thumb && start >= 0)
            {
                if (y - start >= 20) segments.Add((start + TrackTop, y + TrackTop));
                start = -1;
            }
        }
        if (segments.Count != 1) return default;
        var segment = segments[0];
        return new(true, segment.Top, segment.Bottom, segment.Top <= TrackTop + 4, segment.Bottom >= TrackBottom - 4);
    }
}
