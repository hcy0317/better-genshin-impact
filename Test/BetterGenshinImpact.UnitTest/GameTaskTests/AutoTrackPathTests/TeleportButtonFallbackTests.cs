using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class TeleportButtonFallbackTests
{
    [UiRecoveryFact("teapot-panel-20261003.png")]
    public async Task IneffectiveFUsesTheConfirmedButtonBodyAndObservesMapClosing()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var pixels = Cv2.ImRead(UiRecoveryFixtures.PathFor("teapot-panel-20261003.png"));
        var clicks = 0;
        var keys = 0;
        ImageRegion Capture()
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return new ImageRegion(clicks == 0 ? pixels.Clone() : new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black),
                0, 0) { FrameStamp = source.Next() };
        }
        using var first = Capture();
        Assert.True(await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(first, Capture,
            _ => { keys++; return Task.CompletedTask; },
            (_, bounds, _) =>
            {
                Assert.True(bounds.X > 1600); // F提示位于约1440，不是按钮正文。
                clicks++;
                return Task.CompletedTask;
            },
            (milliseconds, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); return Task.CompletedTask; },
            default, new ButtonOcr(), clock));
        Assert.Equal(1, keys);
        Assert.Equal(1, clicks);
    }

    private sealed class ButtonOcr : IOcrService
    {
        public string Ocr(Mat mat) => "";
        public string OcrWithoutDetector(Mat mat) => "";
        public OcrResult OcrResult(Mat mat) => new([new(new(new Point2f(145, 40), new Size2f(64, 28), 0), "传送", 1)]);
    }
}
