using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

[Collection("OfflineNativeDecision")]
public class MapContentReadinessTests
{
    [OfflineNativeDecisionFact]
    public void RecordedEmptyMapCannotCompleteAreaSelection()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var path = Environment.GetEnvironmentVariable("BGI_S68_BLANK_MAP_FRAME");
        Assert.True(File.Exists(path), "Supply the local recording; do not commit private screenshots.");
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0);
        Assert.True(Bv.IsInBigMapUi(frame)); // 外框确实存在，不能以其替代地图内容。
        Assert.False(MapContentReadiness.IsReady(true, frame.CacheGreyMat));
        Assert.Null(System.Windows.Application.Current);
    }

    [OfflineNativeDecisionFact]
    public void RecordedNormalMapRemainsReady()
    {
        var path = Environment.GetEnvironmentVariable("BGI_S68_NORMAL_MAP_FRAME");
        Assert.True(File.Exists(path));
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0);
        Assert.False(MapContentReadiness.IsBlank(frame.CacheGreyMat));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    public void DarkEmptyMapIsVetoedButOrdinaryLowTextureIsNot(int width, int height)
    {
        using var grey = new Mat(height, width, MatType.CV_8UC1, new Scalar(13));
        Assert.True(MapContentReadiness.IsBlank(grey));
        grey.SetTo(new Scalar(75)); // 平坦海面不能仅因低方差被否决。
        Assert.False(MapContentReadiness.IsBlank(grey));
        Assert.False(MapContentReadiness.IsReady(false, grey));
        grey.SetTo(new Scalar(13));
        Cv2.Line(grey, new Point(width / 3, height / 3), new Point(width * 2 / 3, height * 2 / 3), new Scalar(150), 3);
        Assert.False(MapContentReadiness.IsBlank(grey));
        using var empty = new Mat();
        Assert.True(MapContentReadiness.IsBlank(empty));
    }

    [Theory]
    [InlineData("map-rect")]
    [InlineData("map-center")]
    [InlineData("before-drag")]
    public void EmptyContentNeverCallsMatchingOrInputAndCancellationWins(string phase)
    {
        using var frame = new ImageRegion(new Mat(100, 100, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var calls = 0;
        int Match() => MapContentReadiness.Match(frame, _ => ++calls, default, phase);
        Assert.Throws<MapPositionNotRecognizedException>(() => Match());
        Assert.Equal(0, calls);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MapContentReadiness.Match(frame, _ => ++calls, cancelled.Token, phase));
        var clock = new FakeTimeProvider();
        using var parent = UiOperation.Begin("parent", TimeSpan.FromSeconds(1), clock: clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Throws<TimeoutException>(() => Match());
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task DelayedContentWaitsForTwoLoadedFramesWithoutReclicking()
    {
        var clock = new FakeTimeProvider();
        long sequence = 0;
        var clicks = 0;
        using var grey = new Mat(100, 100, MatType.CV_8UC1, new Scalar(13));
        var result = await AreaSelectionClickController.TryApplyAsync(() =>
        {
            ++sequence;
            if (sequence >= 5) grey.SetTo(new Scalar(75));
            return new(sequence, MapContentReadiness.IsReady(true, grey), sequence == 1, sequence == 1);
        }, (_, _) => { clicks++; return Task.FromResult(true); },
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock);
        Assert.True(result);
        Assert.Equal(6, sequence);
        Assert.Equal(1, clicks);
    }
}
