using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class PartyFrameReadinessTests
{
    [OfflineNativeDecisionFact]
    public async Task S56OriginalPartyPageCanBeReadWithoutInventingAnObstruction()
    {
        var path = Environment.GetEnvironmentVariable("BGI_S56_PARTY_FILE");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var factory = new BetterGenshinImpact.Core.Recognition.ONNX.BgiOnnxFactory(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BetterGenshinImpact.Core.Recognition.ONNX.BgiOnnxFactory>.Instance,
            forceCpuOcr: true);
        using var ocr = new BetterGenshinImpact.Core.Recognition.OCR.Paddle.PaddleOcrService(factory,
            BetterGenshinImpact.Core.Recognition.OCR.Paddle.PaddleOcrService.PaddleOcrModelType.V6);
        using var pixels = Cv2.ImRead(path);
        var source = new CaptureFrameSource();
        using var accepted = await SwitchPartyTask.CapturePartyFrameAsync(
            () => new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = source.Next() },
            frame => NativeUiDriver.Read(frame, ocr: ocr, reviveDetector: new(
                BetterGenshinImpact.Core.Recognition.RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr,
                "复苏", "使用道具复苏角色")), (ms, ct) => Task.Delay(ms, ct), default);
        Assert.False(accepted.SrcMat.Empty());
        Assert.True(accepted.FrameStamp.IsFresh(TimeProvider.System, UiSnapshot.RecoveryMaximumAge));
    }

    [Fact]
    public async Task TransientUnknownIsRetriedAndOnlyTheFreshConfirmedFrameIsReturned()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var images = new List<ImageRegion>();
        ImageRegion Capture()
        {
            var image = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3), 0, 0) { FrameStamp = producer.Next() };
            images.Add(image);
            return image;
        }
        using var accepted = await SwitchPartyTask.CapturePartyFrameAsync(Capture,
            image => new UiSnapshot(image.FrameStamp.Sequence) { Party = images.Count >= 2 }
                .WithSource(image.FrameStamp, clock, UiSnapshot.RecoveryMaximumAge),
            (ms, token) => { token.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, clock);
        Assert.Equal(2, images.Count);
        Assert.Same(images[1], accepted);
        Assert.True(images[0].SrcMat.IsDisposed);
        Assert.False(accepted.SrcMat.IsDisposed);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("duplicate")]
    [InlineData("changed-source")]
    [InlineData("obscured")]
    [InlineData("cancel")]
    [InlineData("late-read")]
    public async Task InvalidOrCancelledReadCannotReturnAFrameAndDisposesAllCapturedImages(string fault)
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var other = new CaptureFrameSource(clock);
        var first = producer.Next();
        var images = new List<ImageRegion>();
        using var cancellation = new CancellationTokenSource();
        if (fault == "stale") clock.Advance(TimeSpan.FromSeconds(3));
        var error = await Record.ExceptionAsync(async () =>
        {
            using var unexpected = await SwitchPartyTask.CapturePartyFrameAsync(() =>
            {
                var stamp = fault is "stale" or "duplicate" ? first :
                    fault == "changed-source" && images.Count > 0 ? other.Next() : producer.Next();
                var image = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3), 0, 0) { FrameStamp = stamp };
                images.Add(image);
                return image;
            }, image =>
            {
                if (fault == "late-read") clock.Advance(TimeSpan.FromSeconds(11));
                return new UiSnapshot(image.FrameStamp.Sequence)
                    { Party = images.Count > 1, Prompt = fault == "obscured" }
                    .WithSource(image.FrameStamp, clock, UiSnapshot.RecoveryMaximumAge);
            }, (ms, token) =>
            {
                if (fault == "cancel") cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                clock.Advance(TimeSpan.FromMilliseconds(ms));
                return Task.CompletedTask;
            }, cancellation.Token, clock);
        });
        Assert.NotNull(error);
        if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.All(images, image => Assert.True(image.SrcMat.IsDisposed));
        Assert.InRange((clock.GetUtcNow() - new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero)).TotalSeconds, 0, 13.1);
    }
}
