using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

[Collection("OfflineNativeDecision")]
public class TeleportRejectionTests
{
    [Theory]
    [InlineData("角色无法继续战斗", true)]
    [InlineData("角色 無法 繼續 戰鬥", true)]
    [InlineData("", false)]
    [InlineData("七天神像", false)]
    public void OnlyExplicitDeathToastRejectsTeleport(string text, bool rejected)
    {
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        Assert.Equal(rejected, TeleportRejection.Read(frame, new TextOcr(text)));
    }

    [Fact]
    public async Task RejectedInputCannotBeFollowedBySuccessfulArrivalInOriginalWorld()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var progress = new TeleportArrivalProgress(clock);
        progress.ConfirmMapClosure(source.Next());
        clock.Advance(TimeSpan.FromSeconds(1));
        var captures = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => TeleportPanelConfirmation.WaitForArrivalAsync(progress,
            () => { captures++; return new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() }; },
            frame => { TeleportRejection.Check(frame, "test", new TextOcr("角色无法继续战斗")); return WorldFrameKind.Playable; },
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, TimeSpan.FromSeconds(3), clock));
        Assert.Equal(1, captures);
        Assert.False(progress.Arrived);
    }

    [UiRecoveryTheory("teleport-death-rejected-20261006.png", "teleport-original-world-20261006.png")]
    [InlineData("teleport-death-rejected-20261006.png", true)]
    [InlineData("teleport-original-world-20261006.png", false)]
    public void ActualTransitionUsesCpuOcr(string file, bool rejected)
    {
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var frame = new ImageRegion(Cv2.ImRead(UiRecoveryFixtures.PathFor(file)), 0, 0);
        Assert.Equal(rejected, TeleportRejection.Read(frame, ocr));
        Assert.Null(System.Windows.Application.Current);
    }

    private sealed class TextOcr(string text) : IOcrService
    {
        public string OcrWithoutDetector(Mat mat) => text;
        public string Ocr(Mat mat) => throw new NotSupportedException();
        public OcrResult OcrResult(Mat mat) => throw new NotSupportedException();
    }
}
