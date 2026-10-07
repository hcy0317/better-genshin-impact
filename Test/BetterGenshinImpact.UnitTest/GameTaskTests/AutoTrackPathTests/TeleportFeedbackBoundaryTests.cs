using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class TeleportFeedbackBoundaryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MapFadeAfterSubmittedConfirmationKeepsWaitingWithoutRepeatingInput(bool lingeringMapControls)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var panel = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        foreach (var (name, x, y) in new[] { ("MapScaleButton.png", 30, 440), ("GoTeleport.png", 1440, 960) })
        {
            using var template = Cv2.ImRead(Path.Combine(root!.FullName, "BetterGenshinImpact", "GameTask", "QuickTeleport", "Assets", "1920x1080", name));
            using var area = new Mat(panel, new Rect(x, y, template.Width, template.Height));
            template.CopyTo(area);
        }
        var captures = 0;
        var keys = 0;
        var clicks = 0;
        var arrival = new TeleportArrivalProgress(clock);
        ImageRegion Capture()
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return new ImageRegion(++captures <= (lingeringMapControls ? 12 : 7) ? panel.Clone() : new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0)
                { FrameStamp = source.Next() };
        }
        using var first = Capture();
        Assert.True(await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(first, Capture,
            _ => { keys++; return Task.CompletedTask; }, (_, _, _) => { clicks++; return Task.CompletedTask; },
            (ms, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, new FeedbackOcr(), clock, mapClosed: arrival.ConfirmMapClosure,
            loading: _ => lingeringMapControls && captures >= 8));
        Assert.True(arrival.MapClosureConfirmed);
        Assert.False(arrival.Arrived);
        Assert.Equal(1, keys);
        Assert.Equal(0, clicks);
        Assert.True(clock.GetUtcNow() - first.FrameStamp.CapturedAt < TimeSpan.FromSeconds(2.5));
    }

    [UiRecoveryTheory("teapot-panel-20261003.png")]
    [InlineData("marker")]
    [InlineData("unknown-panel")]
    [InlineData("missing-text")]
    [InlineData("old-frame")]
    [InlineData("expired-ocr")]
    [InlineData("cancel")]
    public async Task InvalidFallbackEvidenceNeverClicks(string fault)
    {
        using var pixels = Cv2.ImRead(UiRecoveryFixtures.PathFor("teapot-panel-20261003.png"));
        using var reference = new ImageRegion(pixels.Clone(), 0, 0);
        using var originalButton = reference.Find(RecognitionAssets.Get("QuickTeleport", "TeleportButton", reference));
        Assert.True(originalButton.IsExist());
        var buttonBounds = new Rect(originalButton.X, originalButton.Y, originalButton.Width, originalButton.Height);
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var ct = new CancellationTokenSource();
        var captures = 0;
        var clicks = 0;
        CaptureFrameStamp original = default;
        ImageRegion Capture()
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var frame = new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = source.Next() };
            if (++captures == 1) original = frame.FrameStamp;
            else if (fault == "old-frame") frame.FrameStamp = original;
            if (captures > 1 && fault == "unknown-panel")
            {
                Cv2.Rectangle(frame.SrcMat, buttonBounds, Scalar.Black, -1);
                Assert.True(Bv.IsInBigMapUi(frame));
            }
            return frame;
        }
        var ocr = new FeedbackOcr
        {
            Title = fault == "marker" ? "点击更改标记名称" : "",
            Missing = fault == "missing-text",
            BeforeBody = () =>
            {
                if (fault == "expired-ocr") clock.Advance(TimeSpan.FromSeconds(3));
                if (fault == "cancel") ct.Cancel();
            }
        };
        using var first = Capture();
        Task<bool> Confirm() => TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(first, Capture,
            _ => Task.CompletedTask, (_, _, _) => { clicks++; return Task.CompletedTask; },
            (ms, token) => { token.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            ct.Token, ocr, clock);
        if (fault == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(Confirm);
        else Assert.False(await Confirm());
        Assert.Equal(0, clicks);
    }

    [UiRecoveryFact("teapot-panel-20261003.png")]
    public async Task ClosureBeforeFallbackCannotCountAsPostClickConfirmation()
    {
        using var pixels = Cv2.ImRead(UiRecoveryFixtures.PathFor("teapot-panel-20261003.png"));
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var captures = 0;
        var clicked = false;
        var postClick = 0;
        ImageRegion Capture()
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            var closed = ++captures == 7 || clicked && ++postClick == 1;
            return new ImageRegion(closed ? new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black) : pixels.Clone(), 0, 0)
                { FrameStamp = source.Next() };
        }
        using var first = Capture();
        Assert.False(await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(first, Capture, _ => Task.CompletedTask,
            (_, _, _) => { clicked = true; return Task.CompletedTask; },
            (ms, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, new FeedbackOcr(), clock));
        Assert.True(clicked);
    }

    [UiRecoveryTheory("teapot-panel-20261003.png")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConfirmedTeleportArrivesEvenIfLoadingFramesAreUnclassified(bool loading)
    {
        using var panel = Cv2.ImRead(UiRecoveryFixtures.PathFor("teapot-panel-20261003.png"));
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var requested = false;
        var arrival = new TeleportArrivalProgress(clock);
        ImageRegion Capture()
        {
            clock.Advance(TimeSpan.FromMilliseconds(1));
            return new ImageRegion(requested ? new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black) : panel.Clone(), 0, 0)
                { FrameStamp = source.Next() };
        }
        Task Delay(int ms, CancellationToken ct) { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }
        using var first = Capture();
        Assert.True(await TeleportPanelConfirmation.TryConfirmWithFeedbackAsync(first, Capture,
            _ => { requested = true; return Task.CompletedTask; }, (_, _, _) => throw new InvalidOperationException("No fallback needed"),
            Delay, default, new FeedbackOcr(), clock,
            frame => arrival.Observe(frame.FrameStamp, loading ? WorldFrameKind.Loading : WorldFrameKind.Unknown),
            arrival.ConfirmMapClosure));
        Assert.Equal(loading, arrival.LoadingObserved);
        Task Wait() => TeleportPanelConfirmation.WaitForArrivalAsync(arrival, Capture, _ => WorldFrameKind.Playable,
            Delay, default, TimeSpan.FromSeconds(2), clock);
        Assert.True(arrival.MapClosureConfirmed);
        await Wait();
        Assert.True(arrival.Arrived);
    }

    private sealed class FeedbackOcr : IOcrService
    {
        internal string Title = "";
        internal bool Missing;
        internal Action? BeforeBody;
        public string Ocr(Mat mat) => Title;
        public string OcrWithoutDetector(Mat mat) => Title;
        public OcrResult OcrResult(Mat mat)
        {
            BeforeBody?.Invoke();
            return Missing ? new([]) : new([new(new(new Point2f(145, 40), new Size2f(64, 28), 0), "传送", 1)]);
        }
    }
}
