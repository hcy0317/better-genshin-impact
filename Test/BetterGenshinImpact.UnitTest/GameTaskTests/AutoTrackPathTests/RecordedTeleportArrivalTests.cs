using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class RecordedTeleportArrivalTests
{
    [UiRecoveryTheory("teapot-arrived-20261004.png", "world-arrived-20261004.png")]
    [InlineData("teapot-arrived-20261004.png")]
    [InlineData("world-arrived-20261004.png")]
    public async Task ActualTimeoutFrameIsPlayableAndCompletesConfirmedTeleportWithoutLoadingSamples(string file)
    {
        using var pixels = Cv2.ImRead(UiRecoveryFixtures.PathFor(file));
        Assert.False(pixels.Empty());
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var source = new CaptureFrameSource(clock);
        var progress = new TeleportArrivalProgress(clock);
        progress.ConfirmMapClosure(source.Next());
        clock.Advance(TimeSpan.FromSeconds(1));
        ImageRegion Capture() => new(pixels.Clone(), 0, 0) { FrameStamp = source.Next() };
        WorldFrameKind Inspect(ImageRegion frame)
        {
            var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame),
                new UnavailableOcr(), "复苏", "使用道具复苏角色");
            var kind = WorldFrameAvailability.ReadWorld(frame, _ => false,
                detector.IsCombatHud, Bv.IsInBigMapUi, new UnavailableOcr());
            Assert.Equal(WorldFrameKind.Playable, kind);
            return kind;
        }
        await TeleportPanelConfirmation.WaitForArrivalAsync(progress, Capture, Inspect,
            (ms, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, TimeSpan.FromSeconds(2), clock);
        Assert.True(progress.Arrived);
        Assert.False(progress.LoadingObserved);
        Assert.True(clock.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
    }

    private sealed class UnavailableOcr : IOcrService
    {
        public string Ocr(Mat mat) => throw new InvalidOperationException("Recorded world should not require OCR");
        public string OcrWithoutDetector(Mat mat) => throw new InvalidOperationException("Recorded world should not require OCR");
        public OcrResult OcrResult(Mat mat) => throw new InvalidOperationException("Recorded world should not require OCR");
    }
}
