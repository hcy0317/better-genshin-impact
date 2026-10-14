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

    [UiRecoveryFact("teapot-panel-20261003.png")]
    public async Task UnconfirmedTeleportRetainsExistingFeedbackBoundariesWithoutAnotherCaptureOrInput()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var pixels = Cv2.ImRead(UiRecoveryFixtures.PathFor("teapot-panel-20261003.png"));
        var captures = 0;
        var keys = 0;
        var clicks = 0;
        var checkpoints = new List<(string Phase, CaptureFrameStamp Source)>();
        ImageRegion Capture()
        {
            captures++;
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = source.Next() };
        }
        using var first = Capture();
        Assert.False(await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(first, Capture,
            _ => { keys++; return Task.CompletedTask; },
            (_, _, _) => { clicks++; return Task.CompletedTask; },
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, new ButtonOcr(), clock,
            checkpoint: (frame, phase) => checkpoints.Add((phase, frame.FrameStamp))));
        Assert.Equal(18, captures); // 初始1 + 按键反馈6 + 兜底1 + 点击反馈10。
        Assert.Equal(1, keys);
        Assert.Equal(1, clicks);
        Assert.Equal(new[] { "initial-feedback-last", "fallback-observation", "click-feedback-last" },
            checkpoints.Select(item => item.Phase));
        Assert.Equal(new long[] { 7, 8, 18 }, checkpoints.Select(item => item.Source.Sequence));
    }

    private sealed class ButtonOcr : IOcrService
    {
        public string Ocr(Mat mat) => "";
        public string OcrWithoutDetector(Mat mat) => "";
        public OcrResult OcrResult(Mat mat) => new([new(new(new Point2f(145, 40), new Size2f(64, 28), 0), "传送", 1)]);
    }
}
