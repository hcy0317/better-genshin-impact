using OpenCvSharp;
using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.Model.GameUI;

internal readonly record struct YasPixelScrollPlan(
    int InputCount,
    double ResidualPixels);

internal sealed class YasScrollInputPacer : IDisposable
{
    private const int HighResolutionThresholdMilliseconds = 10;
    private const uint CreateWaitableTimerHighResolution = 0x00000002;
    private const uint TimerModifyStateAndSynchronize = 0x00100002;
    private EventWaitHandle? _timer;

    internal static bool UsesHighResolutionPacing(int milliseconds) =>
        milliseconds is > 0 and < HighResolutionThresholdMilliseconds;

    internal ValueTask DelayAsync(
        int milliseconds,
        CancellationToken cancellationToken)
    {
        if (milliseconds <= 0) return ValueTask.CompletedTask;
        if (!UsesHighResolutionPacing(milliseconds))
        {
            return new ValueTask(Task.Delay(milliseconds, cancellationToken));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var timer = _timer ??= CreateTimer();
        var dueTime = -milliseconds * 10_000L;
        if (!SetWaitableTimer(
                timer.SafeWaitHandle,
                ref dueTime,
                0,
                IntPtr.Zero,
                IntPtr.Zero,
                false))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        if (!cancellationToken.CanBeCanceled)
        {
            timer.WaitOne();
            return ValueTask.CompletedTask;
        }

        var signaled = WaitHandle.WaitAny([timer, cancellationToken.WaitHandle]);
        if (signaled == 1) cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    private static EventWaitHandle CreateTimer()
    {
        var handle = CreateWaitableTimerEx(
            IntPtr.Zero,
            null,
            CreateWaitableTimerHighResolution,
            TimerModifyStateAndSynchronize);
        if (handle == IntPtr.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "系统不支持 YAS 快速滚动所需的高精度 waitable timer。");
        }

        var timer = new EventWaitHandle(false, EventResetMode.AutoReset);
        timer.SafeWaitHandle.Dispose();
        timer.SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        return timer;
    }

    public void Dispose() => _timer?.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWaitableTimerEx(
        IntPtr timerAttributes,
        string? timerName,
        uint flags,
        uint desiredAccess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWaitableTimer(
        SafeWaitHandle timer,
        ref long dueTime,
        int periodMilliseconds,
        IntPtr completionRoutine,
        IntPtr completionArgument,
        [MarshalAs(UnmanagedType.Bool)] bool resume);
}

internal static class YasPixelScrollPlanner
{
    private const double MaximumRulerColorDistance = 12;
    internal const int CalibrationInputLimit = 5;
    internal const int CalibrationSettleDelayMilliseconds = 80;
    internal const int FirstPageInputIntervalMilliseconds = 20;
    internal const int FastInputIntervalMilliseconds = 2;
    internal const int PageSettleSampleIntervalMilliseconds = 20;
    internal const int MaximumPageSettleSamples = 10;

    internal static YasPixelScrollPlan CreatePlan(
        double rowPitchPixels,
        double pixelsPerInput,
        double residualPixels,
        int rows)
    {
        if (rowPitchPixels <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowPitchPixels));
        if (pixelsPerInput <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerInput));
        if (rows <= 0) throw new ArgumentOutOfRangeException(nameof(rows));

        var totalPixels = residualPixels + rowPitchPixels * rows;
        var inputCount = Math.Max(1, (int)Math.Round(
            totalPixels / pixelsPerInput,
            MidpointRounding.AwayFromZero));
        return new YasPixelScrollPlan(
            inputCount,
            totalPixels - inputCount * pixelsPerInput);
    }

    internal static int FindRulerShift(
        IReadOnlyList<Vec3b> before,
        IReadOnlyList<Vec3b> after,
        int maximumShift)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count != after.Count || before.Count < 8) return 0;
        maximumShift = Math.Min(maximumShift, before.Count - 8);
        if (maximumShift <= 0) return 0;

        var bestShift = 0;
        var bestScore = double.MaxValue;
        for (var shift = 1; shift <= maximumShift; shift++)
        {
            var forward = AverageColorDistance(
                before, 0, after, shift, before.Count - shift);
            var reverse = AverageColorDistance(
                before, shift, after, 0, before.Count - shift);
            var score = Math.Min(forward, reverse);
            if (score >= bestScore) continue;
            bestScore = score;
            bestShift = shift;
        }
        return bestScore <= MaximumRulerColorDistance ? bestShift : 0;
    }

    internal static IReadOnlyList<Vec3b> ReadRuler(Mat capture, Rect rulerRect)
    {
        ArgumentNullException.ThrowIfNull(capture);
        using var ruler = new Mat(capture, rulerRect);
        var colors = new Vec3b[ruler.Height];
        for (var y = 0; y < ruler.Height; y++)
        {
            using var row = new Mat(ruler, new Rect(0, y, ruler.Width, 1));
            var mean = Cv2.Mean(row);
            colors[y] = ruler.Channels() switch
            {
                4 or 3 => new Vec3b(
                    (byte)Math.Round(mean.Val0),
                    (byte)Math.Round(mean.Val1),
                    (byte)Math.Round(mean.Val2)),
                1 => new Vec3b(
                    (byte)Math.Round(mean.Val0),
                    (byte)Math.Round(mean.Val0),
                    (byte)Math.Round(mean.Val0)),
                var channels => throw new InvalidOperationException(
                    $"不支持的 YAS ruler 截图通道数：{channels}")
            };
        }
        return colors;
    }

    internal static bool IsRulerStable(
        IReadOnlyList<Vec3b> before,
        IReadOnlyList<Vec3b> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        if (before.Count != after.Count || before.Count == 0) return false;
        return AverageColorDistance(
            before, 0, after, 0, before.Count) <= 2;
    }

    private static double AverageColorDistance(
        IReadOnlyList<Vec3b> left,
        int leftStart,
        IReadOnlyList<Vec3b> right,
        int rightStart,
        int count)
    {
        double total = 0;
        for (var index = 0; index < count; index++)
        {
            var first = left[leftStart + index];
            var second = right[rightStart + index];
            total += Math.Abs(first.Item0 - second.Item0);
            total += Math.Abs(first.Item1 - second.Item1);
            total += Math.Abs(first.Item2 - second.Item2);
        }
        return total / (count * 3d);
    }

}

internal static class ArtifactRowScrollPlanner
{
    internal static int RowsToScroll(int totalRows, int visibleRows, int scrolledRows)
    {
        if (totalRows <= 0) throw new ArgumentOutOfRangeException(nameof(totalRows));
        if (visibleRows <= 0) throw new ArgumentOutOfRangeException(nameof(visibleRows));
        if (scrolledRows < 0) throw new ArgumentOutOfRangeException(nameof(scrolledRows));

        return Math.Min(visibleRows, Math.Max(0, totalRows - visibleRows - scrolledRows));
    }

}

internal static class ArtifactGridAlignmentPlanner
{
    internal static bool IsAligned(
        IEnumerable<Rect> items,
        Size captureSize,
        Rect roi,
        int columns)
    {
        ArgumentNullException.ThrowIfNull(items);
        if (captureSize.Width <= 0 || captureSize.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(captureSize));
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));

        var template = ArtifactGridLayout.CellsInRoi(captureSize, roi);
        if (template.Count < columns * 2) return false;
        var firstRow = template[0].Rect;
        var secondRow = template[columns].Rect;
        var yTolerance = Math.Max(4, (int)Math.Round(firstRow.Height * 0.12));
        var heightTolerance = Math.Max(4, (int)Math.Round(firstRow.Height * 0.10));
        var candidates = items.Where(item =>
            Math.Abs(item.Height - firstRow.Height) <= heightTolerance).ToArray();
        var minimumItemsPerRow = Math.Max(1, columns - 1);

        return candidates.Count(item =>
                Math.Abs(item.Y - firstRow.Y) <= yTolerance)
            >= minimumItemsPerRow
            && candidates.Count(item =>
                Math.Abs(item.Y - secondRow.Y) <= yTolerance)
            >= minimumItemsPerRow;
    }

    internal static double? VerticalOffsetPixels(
        IEnumerable<Rect> items,
        Size captureSize,
        Rect roi,
        int columns)
    {
        ArgumentNullException.ThrowIfNull(items);
        var template = ArtifactGridLayout.CellsInRoi(captureSize, roi);
        if (columns <= 0 || template.Count < columns * 2) return null;
        var firstRow = template[0].Rect;
        var heightTolerance = Math.Max(4, (int)Math.Round(firstRow.Height * 0.10));
        var rowTolerance = Math.Max(2, (int)Math.Round(firstRow.Height * 0.04));
        var candidates = items
            .Where(item => Math.Abs(item.Height - firstRow.Height) <= heightTolerance)
            .OrderBy(item => item.Y)
            .ToArray();
        var minimumItemsPerRow = Math.Max(1, columns - 1);
        if (candidates.Length < minimumItemsPerRow) return null;

        var clusters = new List<List<int>>();
        foreach (var item in candidates)
        {
            var cluster = clusters.FirstOrDefault(group =>
                Math.Abs(group.Average() - item.Y) <= rowTolerance);
            if (cluster is null)
            {
                cluster = [];
                clusters.Add(cluster);
            }
            cluster.Add(item.Y);
        }

        var actualFirstRow = clusters
            .Where(group => group.Count >= minimumItemsPerRow)
            .Select(group => group.Average())
            .OrderBy(y => y)
            .Cast<double?>()
            .FirstOrDefault();
        return actualFirstRow.HasValue
            ? actualFirstRow.Value - firstRow.Y
            : null;
    }

    internal static int CorrectionInputs(double verticalOffset, double pixelsPerInput)
    {
        if (pixelsPerInput <= 0)
            throw new ArgumentOutOfRangeException(nameof(pixelsPerInput));
        if (Math.Abs(verticalOffset) <= 1) return 0;
        var count = Math.Max(1, (int)Math.Round(
            Math.Abs(verticalOffset) / pixelsPerInput,
            MidpointRounding.AwayFromZero));
        return verticalOffset > 0 ? count : -count;
    }

}

internal static class ArtifactGridLayout
{
    private const double FirstCellX = 99;
    private const double FirstCellY = 149.5;
    private const double CellWidth = 102;
    private const double CellHeight = 126;
    private const double HorizontalGap = 20;
    private const double VerticalGap = 20;
    private const double RulerX = 272;
    private const double RulerTop = 102;
    private const double RulerWidth = 2;
    private const double RulerHeight = 123;
    private const double RowPitch = 146;

    internal static IReadOnlyList<GridCell> CellsInRoi(Size captureSize, Rect roi)
    {
        var cells = new List<GridCell>(40);

        for (var row = 0; row < 5; row++)
        {
            for (var column = 0; column < 8; column++)
            {
                var absolute = ArtifactUiCoordinateMapper.ToCaptureRect(
                    captureSize,
                    FirstCellX + column * (CellWidth + HorizontalGap),
                    FirstCellY + row * (CellHeight + VerticalGap),
                    CellWidth,
                    CellHeight);
                cells.Add(new GridCell(new Rect(
                    absolute.X - roi.X,
                    absolute.Y - roi.Y,
                    absolute.Width,
                    absolute.Height))
                {
                    RowNum = row,
                    ColNum = column
                });
            }
        }

        return cells;
    }

    internal static Rect RulerRect(Size captureSize) =>
        ArtifactUiCoordinateMapper.ToCaptureRect(
            captureSize,
            RulerX,
            RulerTop,
            RulerWidth,
            RulerHeight);

    internal static double RowPitchPixels(Size captureSize)
    {
        var top = ArtifactUiCoordinateMapper.ToCapturePoint(captureSize, 0, 0);
        var next = ArtifactUiCoordinateMapper.ToCapturePoint(
            captureSize, 0, RowPitch);
        return next.Y - top.Y;
    }

}
