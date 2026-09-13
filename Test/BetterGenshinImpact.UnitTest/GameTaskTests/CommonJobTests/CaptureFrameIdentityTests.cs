using Fischless.GameCapture;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class CaptureFrameIdentityTests
{
    [Fact]
    public void SelectionHandsTheConfirmedFrameToItsOwnerWithoutDisposingOrReissuingIt()
    {
        var source = new CaptureFrameSource().Next();
        var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source };
        var selected = new Avatar.AvatarSelectionResult(true, source, null, frame);
        using var handedOff = selected.TakeFrame();
        selected.Dispose();
        Assert.Same(frame, handedOff);
        Assert.False(handedOff!.SrcMat.IsDisposed);
        Assert.Equal(source, handedOff.FrameStamp);
        Assert.Null(selected.TakeFrame());
    }

    [Fact]
    public void CloningAFrameCannotMakeOldEvidenceNew()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new GameCaptureFrame(new Mat(8, 8, MatType.CV_8UC3, Scalar.Black),
            stamp: source.Next());
        clock.Advance(TimeSpan.FromMilliseconds(151));
        using var clone = frame.Clone();

        Assert.Equal(frame.Stamp, clone.Stamp);
        Assert.False(clone.Stamp.IsFresh(clock, TimeSpan.FromMilliseconds(150)));
        Assert.False(clone.Stamp.IsAfter(frame.Stamp));
    }

    [Fact]
    public void ADelayedProducerCannotUseDeliveryTimeAsAcquisitionTime()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var capturedAt = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromSeconds(1));
        var delayed = source.Next(capturedTimestamp: capturedAt);
        Assert.False(delayed.IsFresh(clock, TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public void RestartedOrUnknownSourcesCannotContinueAnOldFrameSequence()
    {
        var source = new CaptureFrameSource(new FakeTimeProvider());
        var before = source.Next();
        Assert.True(source.Next().IsAfter(before));
        source.Restart();
        var restarted = source.Next();
        Assert.NotEqual(before.SessionId, restarted.SessionId);
        Assert.False(restarted.IsAfter(before));
        Assert.False(default(CaptureFrameStamp).IsKnown);
    }

    [Fact]
    public void NormalizationAndCroppingKeepTheProducerIdentity()
    {
        var source = new CaptureFrameSource(new FakeTimeProvider());
        using var frame = new GameCaptureFrame(new Mat(1440, 2560, MatType.CV_8UC3, Scalar.Black),
            stamp: source.Next());
        using var content = new CaptureContent(frame, 0, 50, new FakeSystemInfo(new(0, 0, 2560, 1440), 1));
        using var crop = content.CaptureRectArea.DeriveCrop(10, 10, 30, 30);
        Assert.Equal(1920, content.CaptureRectArea.Width);
        Assert.Equal(frame.Stamp, content.CaptureRectArea.FrameStamp);
        Assert.Equal(frame.Stamp, crop.FrameStamp);
    }
}
