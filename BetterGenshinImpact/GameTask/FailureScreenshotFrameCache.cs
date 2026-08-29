using System;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask;

internal sealed class FailureScreenshotFrameCache(TimeSpan minimumUpdateInterval) : IDisposable
{
    private readonly object _syncRoot = new();
    private Mat? _frame;
    private DateTimeOffset _updatedAt;

    internal bool TryUpdate(Mat source, DateTimeOffset now)
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
        }

        replaced?.Dispose();
        return true;
    }

    internal Mat? TryClone(DateTimeOffset now, out TimeSpan age)
    {
        lock (_syncRoot)
        {
            if (_frame == null)
            {
                age = TimeSpan.Zero;
                return null;
            }

            age = now >= _updatedAt ? now - _updatedAt : TimeSpan.Zero;
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
        }

        removed?.Dispose();
    }

    public void Dispose()
    {
        Clear();
    }
}
