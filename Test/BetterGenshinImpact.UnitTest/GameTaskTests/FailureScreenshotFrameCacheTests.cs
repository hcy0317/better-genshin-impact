using BetterGenshinImpact.GameTask;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class FailureScreenshotFrameCacheTests
{
    [Fact]
    public void CacheReturnsIndependentCloneOfLastAcceptedFrame()
    {
        using var cache = new FailureScreenshotFrameCache(TimeSpan.FromSeconds(1));
        using var first = new Mat(2, 2, MatType.CV_8UC3, new Scalar(1, 2, 3));
        var acceptedAt = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero);

        Assert.True(cache.TryUpdate(first, acceptedAt));
        first.SetTo(new Scalar(9, 9, 9));

        using var cached = cache.TryClone(acceptedAt.AddSeconds(2), out var age);
        Assert.NotNull(cached);
        Assert.Equal(new Vec3b(1, 2, 3), cached!.At<Vec3b>(0, 0));
        Assert.Equal(TimeSpan.FromSeconds(2), age);
    }

    [Fact]
    public void CacheThrottlesUpdatesButAcceptsNewerFrameAfterInterval()
    {
        using var cache = new FailureScreenshotFrameCache(TimeSpan.FromSeconds(1));
        using var first = new Mat(1, 1, MatType.CV_8UC1, new Scalar(1));
        using var tooSoon = new Mat(1, 1, MatType.CV_8UC1, new Scalar(2));
        using var later = new Mat(1, 1, MatType.CV_8UC1, new Scalar(3));
        var startedAt = new DateTimeOffset(2026, 8, 25, 1, 0, 0, TimeSpan.Zero);

        Assert.True(cache.TryUpdate(first, startedAt));
        Assert.False(cache.TryUpdate(tooSoon, startedAt.AddMilliseconds(999)));
        Assert.True(cache.TryUpdate(later, startedAt.AddSeconds(1)));

        using var cached = cache.TryClone(startedAt.AddSeconds(1), out _);
        Assert.NotNull(cached);
        Assert.Equal((byte)3, cached!.At<byte>(0, 0));
    }

    [Fact]
    public void ClearRemovesFrameBeforeNextRun()
    {
        using var cache = new FailureScreenshotFrameCache(TimeSpan.Zero);
        using var source = new Mat(1, 1, MatType.CV_8UC1, new Scalar(1));
        var now = DateTimeOffset.UtcNow;
        cache.TryUpdate(source, now);

        cache.Clear();

        Assert.Null(cache.TryClone(now, out _));
    }
}
