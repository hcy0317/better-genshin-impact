using System;
using System.Collections.Generic;
using System.Linq;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal enum PartyListEnd { Unknown, More, End }
internal readonly record struct PartyListRow(string Text, Rect Bounds);
internal readonly record struct PartyListDragPlan(CaptureFrameStamp Source, Point2d Start, Point2d Finish, Rect Viewport, double ContentAdvance);

internal sealed record PartyListPageObservation(CaptureFrameStamp Source, PartyListEnd End, Rect Track, Rect Thumb, string NamesSignature)
{
    internal IReadOnlyList<PartyListRow> Rows { get; init; } = Array.Empty<PartyListRow>();

    internal bool TryPlanPage(Rect content, out PartyListDragPlan plan)
    {
        plan = default;
        if (End != PartyListEnd.More || !Source.IsKnown || Thumb.Width <= 0 || Thumb.Height <= 0 ||
            Thumb.Height >= Track.Height || content.Width <= 0) return false;
        var top = Math.Max(Track.Top, content.Top); var bottom = Math.Min(Track.Bottom, content.Bottom);
        if (bottom <= top) return false;
        var viewport = new Rect(content.X, top, content.Width, bottom - top);
        var labels = Rows.Where(r => r.Bounds.Width > 0 && r.Bounds.Height > 0 && r.Bounds.X >= content.X &&
            r.Bounds.Right <= content.Right && r.Bounds.Top >= top && r.Bounds.Bottom <= bottom && !string.IsNullOrWhiteSpace(r.Text))
            .Select(r => r.Bounds).OrderBy(r => r.Y).ToArray();
        var physicalRows = new List<Rect>();
        foreach (var box in labels)
        {
            if (physicalRows.Count == 0 || box.Top >= physicalRows[^1].Bottom) { physicalRows.Add(box); continue; }
            var previous = physicalRows[^1];
            var overlap = Math.Min(previous.Bottom, box.Bottom) - Math.Max(previous.Top, box.Top);
            if (overlap * 2 < Math.Min(previous.Height, box.Height) || box.Top - previous.Top > Math.Min(previous.Height, box.Height) / 2d)
                return false; // Partial/ambiguous vertical overlap is not another confirmed physical row.
            var left = Math.Min(previous.X, box.X); var rowTop = Math.Min(previous.Y, box.Y);
            physicalRows[^1] = new Rect(left, rowTop, Math.Max(previous.Right, box.Right) - left, Math.Max(previous.Bottom, box.Bottom) - rowTop);
        }
        var rows = physicalRows.ToArray();
        if (rows.Length < 2) return false;
        var labelHeight = rows.Max(r => r.Height);
        // Separators and missed OCR rows can create larger gaps; confirmed row anchors still bound overlap.
        var gaps = rows.Zip(rows.Skip(1), (a, b) => b.Y - a.Y).Where(y => y >= labelHeight * 2).ToArray();
        if (gaps.Length == 0) return false;
        var pitch = gaps.Min();
        if (viewport.Height <= 2 * pitch) return false;
        // Keep the last two confirmed rows fully visible after the move, including one label-height margin.
        var contentAdvance = Math.Min(viewport.Height - 2d * pitch, rows[^2].Top - top - labelHeight);
        if (contentAdvance < pitch / 2d) return false;
        var thumbAdvance = Math.Round(contentAdvance * Thumb.Height / viewport.Height, MidpointRounding.AwayFromZero);
        var targetTop = Math.Min(Track.Bottom - Thumb.Height, Thumb.Top + thumbAdvance);
        if (targetTop - Thumb.Top <= 2 || targetTop < Track.Top) return false;
        var x = Thumb.X + Thumb.Width / 2d; var y = Thumb.Y + Thumb.Height / 2d;
        plan = new(Source, new Point2d(x, y), new Point2d(x, targetTop + Thumb.Height / 2d), viewport,
            (targetTop - Thumb.Top) * viewport.Height / (double)Thumb.Height);
        return true;
    }

    // This is the existing party scrollbar used by the original (700,125) top reset, scaled to capture pixels.
    internal static PartyListPageObservation Read(Mat image, CaptureFrameStamp source, double scale, IEnumerable<(string Text, int Y)> names)
        => Read(image, source, scale, names.Select(n => new PartyListRow(n.Text, new Rect(0, n.Y, 0, 0))));

    internal static PartyListPageObservation Read(Mat image, CaptureFrameStamp source, double scale, IEnumerable<PartyListRow> observedRows)
    {
        var rows = observedRows.ToArray();
        var names = rows.Select(r => (Text: r.Text, Y: r.Bounds.Y));
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
        return new(source, end, track, thumb, namesSignature) { Rows = rows };
    }

    internal bool AdvancesFrom(PartyListPageObservation previous, long inputCompleted, TimeProvider clock) =>
        End != PartyListEnd.Unknown && previous.End == PartyListEnd.More && Source.IsAfter(previous.Source) &&
        Source.TimestampFrequency == clock.TimestampFrequency && Source.CapturedTimestamp > previous.Source.CapturedTimestamp &&
        clock.GetElapsedTime(inputCompleted, Source.CapturedTimestamp) >= TimeSpan.FromMilliseconds(60) &&
        Source.IsFresh(clock, TimeSpan.FromSeconds(2)) && NamesSignature != previous.NamesSignature &&
        (Thumb.Top > previous.Thumb.Top + 1 || Thumb.Bottom > previous.Thumb.Bottom + 1);
}
