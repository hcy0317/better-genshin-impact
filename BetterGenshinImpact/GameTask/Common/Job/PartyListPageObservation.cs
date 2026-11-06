using System;
using System.Collections.Generic;
using System.Linq;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal enum PartyListEnd { Unknown, More, End }
internal sealed record PartyListPageObservation(CaptureFrameStamp Source, PartyListEnd End, Rect Track, Rect Thumb, string NamesSignature)
{
    // This is the existing party scrollbar used by the original (700,125) top reset, scaled to capture pixels.
    internal static PartyListPageObservation Read(Mat image, CaptureFrameStamp source, double scale, IEnumerable<(string Text, int Y)> names)
    {
        var track = new Rect((int)Math.Round(694 * scale), (int)Math.Round(105 * scale),
            Math.Max(8, (int)Math.Round(16 * scale)), Math.Max(1, (int)Math.Round(850 * scale)));
        // OCR names/positions are independent evidence from scrollbar geometry.
        var namesSignature = string.Join("|", names.OrderBy(n => n.Y).Select(n => n.Y + ":" + n.Text.Trim()));
        PartyListPageObservation Unknown() => new(source, PartyListEnd.Unknown, track, default, namesSignature);
        if (!source.IsKnown || image.Empty() || image.Channels() < 3 || track.X < 0 || track.Y < 0 ||
            track.Right > image.Width || track.Bottom > image.Height) return Unknown();
        using var strip = new Mat(image, track);
        using var hsv = new Mat(); Cv2.CvtColor(strip, hsv, image.Channels() == 4 ? ColorConversionCodes.BGRA2BGR : ColorConversionCodes.BGR2HSV);
        if (image.Channels() == 4) { using var bgr = hsv.Clone(); Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV); }
        using var mask = new Mat(); Cv2.InRange(hsv, new Scalar(0, 0, 150), new Scalar(180, 60, 255), mask);
        using var labels = new Mat(); using var stats = new Mat(); using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids, PixelConnectivity.Connectivity8, MatType.CV_32S);
        var candidates = new List<Rect>();
        for (var label = 1; label < count; label++)
        {
            var x = stats.At<int>(label, 0); var y = stats.At<int>(label, 1);
            var width = stats.At<int>(label, 2); var height = stats.At<int>(label, 3);
            if (width < Math.Max(2, (int)Math.Round(2 * scale)) || width > (int)Math.Round(12 * scale) ||
                height < Math.Max(16, (int)Math.Round(20 * scale))) continue;
            candidates.Add(new Rect(track.X + x, track.Y + y, width, height));
        }
        if (candidates.Count != 1) return Unknown();
        var thumb = candidates[0];
        var end = thumb.Bottom >= track.Bottom - Math.Max(4, (int)Math.Round(8 * scale)) ? PartyListEnd.End : PartyListEnd.More;
        return new(source, end, track, thumb, namesSignature);
    }

    internal bool AdvancesFrom(PartyListPageObservation previous, long inputCompleted, TimeProvider clock) =>
        End != PartyListEnd.Unknown && previous.End == PartyListEnd.More && Source.IsAfter(previous.Source) &&
        Source.TimestampFrequency == clock.TimestampFrequency && Source.CapturedTimestamp > previous.Source.CapturedTimestamp &&
        clock.GetElapsedTime(inputCompleted, Source.CapturedTimestamp) >= TimeSpan.FromMilliseconds(60) &&
        Source.IsFresh(clock, TimeSpan.FromSeconds(2)) && NamesSignature != previous.NamesSignature &&
        (Thumb.Top > previous.Thumb.Top + 1 || Thumb.Bottom > previous.Thumb.Bottom + 1);
}
