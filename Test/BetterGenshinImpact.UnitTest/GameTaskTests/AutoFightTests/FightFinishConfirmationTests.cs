using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class FightFinishConfirmationTests
{
    [Theory]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    public void PathingAlwaysRequiresEndConfirmationButStandaloneTimedModeRemainsAvailable(
        bool configured, bool isPathing, bool expected) =>
        Assert.Equal(expected, AutoFightHandler.RequireFinishDetection(configured, isPathing));

    [Theory]
    [InlineData("战斗时间上限")]
    [InlineData("找敌次数上限")]
    [InlineData("没有可用动作")]
    public void UnconfirmedNormalReturnCannotStartTheNextRoute(string reason)
    {
        var nextRouteStarted = false;
        Assert.Throws<BetterGenshinImpact.GameTask.CombatNotFinishedException>(() =>
        {
            AutoFightTask.EnsureFightFinishConfirmed(true, false, reason);
            nextRouteStarted = true;
        });
        Assert.False(nextRouteStarted);
        AutoFightTask.EnsureFightFinishConfirmed(true, true, reason);
        AutoFightTask.EnsureFightFinishConfirmed(false, false, reason);
    }

    [Fact]
    public void OneFrameColorFlashCannotConfirmFightEnd()
    {
        var requestedAt = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var detector = new PartySetupFinishDetector(
            new(1, DateTimeOffset.UnixEpoch, 1920, 1080, false, 10), requestedAt);

        Assert.False(detector.Observe(new(2, requestedAt.AddMilliseconds(100), 1920, 1080, true, 11)));
        Assert.False(detector.Observe(new(3, requestedAt.AddMilliseconds(200), 1920, 1080, false, 12)));
    }

    [Fact]
    public void OnlyTwoDistinctPostInputBarImagesConfirmEnd()
    {
        var at = DateTimeOffset.UnixEpoch;
        var detector = new PartySetupFinishDetector(new(1, at, 1920, 1080, false, 10), at);
        Assert.False(detector.Observe(new(2, at.AddMilliseconds(100), 1920, 1080, true, 11)));
        Assert.False(detector.Observe(new(2, at.AddMilliseconds(200), 1920, 1080, true, 12)));
        Assert.False(detector.Observe(new(3, at.AddMilliseconds(300), 1920, 1080, true, 11)));
        Assert.True(detector.Observe(new(4, at.AddMilliseconds(400), 1920, 1080, true, 12)));
    }

    [Fact]
    public void PreExistingSceneColorsDoNotConfirmTheNewPartyRequest()
    {
        var at = DateTimeOffset.UnixEpoch;
        var detector = new PartySetupFinishDetector(new(1, at, 1920, 1080, true, 10), at);
        Assert.False(detector.Observe(new(2, at.AddMilliseconds(100), 1920, 1080, true, 11)));
        Assert.False(detector.Observe(new(3, at.AddMilliseconds(200), 1920, 1080, true, 12)));
    }

    [Fact]
    public void CaptureSizeChangeCannotCompleteAPartiallyConfirmedBar()
    {
        var at = DateTimeOffset.UnixEpoch;
        var detector = new PartySetupFinishDetector(new(1, at, 1920, 1080, false, 10), at);
        Assert.False(detector.Observe(new(2, at.AddMilliseconds(100), 1920, 1080, true, 11)));
        Assert.False(detector.Observe(new(3, at.AddMilliseconds(200), 1280, 720, true, 12)));
        Assert.False(detector.Observe(new(4, at.AddMilliseconds(300), 1920, 1080, true, 13)));
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    public void ContinuousBarFeaturesAreRecognizedAtTheCaptureScale(int width, int height)
    {
        using var image = new ImageRegion(new Mat(height, width, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var scale = width / 1920d;
        var y = (int)Math.Round(50 * scale);
        Cv2.Line(image.SrcMat, new Point((int)Math.Round(767 * scale), y),
            new Point((int)Math.Round(769 * scale), y), Scalar.White);
        Cv2.Line(image.SrcMat, new Point((int)Math.Round(786 * scale), y),
            new Point((int)Math.Round(794 * scale), y), new Scalar(0, 255, 255));

        Assert.True(AutoFightTask.IsPartySetupProgressBarVisible(image));
    }

    [Fact]
    public void TwoUnrelatedScenePixelsAreNotAPartyLoadingBar()
    {
        using var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        image.SrcMat.Set(50, 768, new Vec3b(255, 255, 255));
        image.SrcMat.Set(50, 790, new Vec3b(0, 255, 255));

        Assert.False(AutoFightTask.IsPartySetupProgressBarVisible(image));
    }
}
