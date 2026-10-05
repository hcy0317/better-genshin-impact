using Fischless.GameCapture.BitBlt;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class DesktopBitBltRegionTests
{
    [Fact]
    public void CapturesExactlyClientPixelsOnMonitorWithNegativeCoordinates()
    {
        Assert.True(DesktopBitBltRegion.TryCreate(new POINT(-1900, 30), 1800, 1000,
            1800, 1000, new RECT(-1920, 0, 1920, 1080), out var region));
        Assert.Equal(new RECT(-1900, 30, -100, 1030), region);
    }

    [Theory]
    [InlineData(-1921, 0, 1920, 1080)]
    [InlineData(1, 0, 1920, 1080)]
    [InlineData(0, -1, 1920, 1080)]
    [InlineData(0, 1, 1920, 1080)]
    [InlineData(0, 0, 1919, 1080)]
    [InlineData(0, 0, 1920, 1079)]
    [InlineData(int.MaxValue, 0, 1920, 1080)]
    public void RejectsOffscreenOrResizedWindowInsteadOfPublishingOtherDesktopPixels(
        int x, int y, int width, int height)
    {
        Assert.False(DesktopBitBltRegion.TryCreate(new POINT(x, y), width, height,
            1920, 1080, new RECT(-1920, 0, 1920, 1080), out _));
    }
}
