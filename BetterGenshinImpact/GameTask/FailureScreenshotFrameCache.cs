using System;
using OpenCvSharp;
using Fischless.GameCapture;

namespace BetterGenshinImpact.GameTask;

internal sealed class FailureScreenshotFrameCache(TimeSpan minimumUpdateInterval) : IDisposable
{
    private readonly object _syncRoot = new();
    private Mat? _frame;
    private DateTimeOffset _updatedAt;
    private CaptureFrameStamp _stamp;

    internal bool TryUpdate(Mat source, DateTimeOffset now, CaptureFrameStamp stamp = default)
    {
        Mat? replaced;
        lock (_syncRoot)
        {
            if (_frame != null && now >= _updatedAt && now - _updatedAt < minimumUpdateInterval)
            {
                return false;
            }

            var next = source.Clone();
            replaced = _frame;
            _frame = next;
            _updatedAt = now;
            _stamp = stamp;
        }

        replaced?.Dispose();
        return true;
    }

    internal Mat? TryClone(DateTimeOffset now, out TimeSpan age)
        => TryClone(now, out age, out _);

    internal Mat? TryClone(DateTimeOffset now, out TimeSpan age, out CaptureFrameStamp stamp)
    {
        lock (_syncRoot)
        {
            if (_frame == null)
            {
                age = TimeSpan.Zero;
                stamp = default;
                return null;
            }

            age = now >= _updatedAt ? now - _updatedAt : TimeSpan.Zero;
            stamp = _stamp;
            return _frame.Clone();
        }
    }

    internal void Clear()
    {
        Mat? removed;
        lock (_syncRoot)
        {
            removed = _frame;
            _frame = null;
            _updatedAt = default;
            _stamp = default;
        }

        removed?.Dispose();
    }

    public void Dispose()
    {
        Clear();
    }
}
