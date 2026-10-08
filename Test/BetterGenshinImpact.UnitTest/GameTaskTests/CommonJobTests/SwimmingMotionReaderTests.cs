using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Core.Recognition.OCR;
using OpenCvSharp;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Time.Testing;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class SwimmingMotionReaderTests
{
    [OfflineNativeDecisionFact]
    public void RecordedSwimmingMustReachTheSharedMotionReader()
    {
        var path = Environment.GetEnvironmentVariable("BGI_RECORDED_SWIMMING_FRAME");
        Assert.True(File.Exists(path), "需要显式提供本次故障原图");
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0);
        Assert.Equal("Swim", Bv.GetMotionStatus(frame).ToString());
        Assert.Equal(MotionStatus.Swim, CombatMotionReader.Read(frame, true, new NoPromptOcr()));
        foreach (var width in new[] { 1280, 2560 })
        {
            using var resized = new Mat();
            Cv2.Resize(frame.SrcMat, resized, new Size(width, width * 9 / 16));
            using var scaled = new ImageRegion(resized.Clone(), 0, 0);
            Assert.True(SwimmingMotionReader.IsSwimming(scaled), $"width={width}");
        }
        Assert.Null(System.Windows.Application.Current);
    }

    [Theory]
    [InlineData("inactive-zhongli-20260911.png")]
    [InlineData("s32-mining-hud-20260916.png")]
    [InlineData("s32-map-marker-20260916.png")]
    [InlineData("s32-food-revive-20260916.png")]
    public void OrdinaryHudAndOverlaysDoNotBecomeSwimming(string file)
    {
        using var image = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", file)), 0, 0);
        Assert.False(SwimmingMotionReader.IsSwimming(image));
    }

    [Fact]
    public void AYellowNoisePixelAndUnsupportedLayoutDoNotGrantSwimming()
    {
        using var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        pixels.Set(1028, 1823, new Vec3b(40, 228, 250));
        using var image = new ImageRegion(pixels.Clone(), 0, 0);
        Assert.False(SwimmingMotionReader.IsSwimming(image));
        using var unsupported = new ImageRegion(new Mat(720, 1000, MatType.CV_8UC3, new Scalar(40, 228, 250)), 0, 0);
        Assert.False(SwimmingMotionReader.IsSwimming(unsupported));
    }

    [OfflineNativeDecisionFact]
    public async Task RecordedFrozenSwimmingPixelsCannotCountAsMapInputFeedbackWithNewProducerStamps()
    {
        var path = Environment.GetEnvironmentVariable("BGI_RECORDED_SWIMMING_FRAME");
        Assert.True(File.Exists(path), "需要本次故障原图，不能用合成场景替代");
        using var pixels = Cv2.ImRead(path!);
        var time = new FakeTimeProvider();
        var source = new CaptureFrameSource(time);
        var inputs = 0;
        var result = await BigMapOpenAttempt.RunAsync(
            () => new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = source.Next() }, Bv.IsInBigMapUi,
            _ => null, () => { }, () =>
            {
                inputs++;
                new WindowsInputMessageDispatcher(null, events => (uint)events.Length, () => 0).DispatchInput(new User32.INPUT[2]);
            }, (ms, token) => { token.ThrowIfCancellationRequested(); time.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, TimeSpan.FromMilliseconds(500), time, () => "original pixels; isolated input replay");
        Assert.Equal(BigMapOpenOutcome.NoVisualFeedback, result);
        Assert.Equal(1, inputs);
        Assert.Null(System.Windows.Application.Current);
    }

    private sealed class NoPromptOcr : IOcrService
    {
        public string Ocr(Mat mat) => "";
        public string OcrWithoutDetector(Mat mat) => "";
        public OcrResult OcrResult(Mat mat) => throw new InvalidOperationException("No full OCR is needed for the swimming frame.");
    }
}
