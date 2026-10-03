using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class HandbookSlowRecognitionTests
{
    [UiRecoveryTheory("handbook-commission-20261003.png")]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedClassificationAfterMismatchDisposesAndCannotRestoreTheOldCache(bool otherSession)
    {
        using var fixture = new RecordedHandbook();
        using var original = fixture.Pixels.Clone();
        fixture.Driver.Capture();
        var cache = typeof(NativeUiDriver).GetField("_handbook", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(fixture.Driver)!;
        var pixels = (Mat)typeof(KnownHandbookFrame).GetField("_pixels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(cache)!;
        if (otherSession) fixture.OtherSession = true;
        else Cv2.Rectangle(fixture.Pixels, new Rect(700, 350, 600, 300), Scalar.Black, -1);
        fixture.FailClassification = true;
        Assert.Throws<IOException>(() => fixture.Driver.Capture());
        Assert.True(pixels.IsDisposed);
        fixture.OtherSession = fixture.FailClassification = false;
        original.CopyTo(fixture.Pixels);
        Assert.False(fixture.Driver.Capture().HasUsableEvidence);
        Assert.Equal(3, fixture.Classifications);
    }

    [UiRecoveryFact("handbook-commission-20261003.png")]
    public async Task SlowClassificationCanRecoverWithAFreshUnchangedHandbookFrame()
    {
        using var fixture = new RecordedHandbook();
        Assert.False(fixture.Driver.Capture().HasUsableEvidence);
        var fresh = fixture.Driver.Capture();
        Assert.True(fresh.HasUsableEvidence);
        Assert.True(await fixture.Driver.ActAsync(UiAction.Escape, fresh, default));
        Assert.Equal(1, fixture.Escapes);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [UiRecoveryTheory("handbook-commission-20261003.png")]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedOrDimmedHandbookCannotReuseTheOldClassification(bool dim)
    {
        using var fixture = new RecordedHandbook();
        fixture.Driver.Capture();
        if (dim) fixture.Pixels.ConvertTo(fixture.Pixels, -1, .5);
        else Cv2.Rectangle(fixture.Pixels, new Rect(700, 350, 600, 300), Scalar.Black, -1);
        var changed = fixture.Driver.Capture();
        Assert.False(changed.HasUsableEvidence);
        Assert.Equal(0, fixture.Escapes);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    private sealed class RecordedHandbook : IDisposable
    {
        internal readonly Mat Pixels = Cv2.ImRead(UiRecoveryFixtures.PathFor("handbook-commission-20261003.png"));
        internal readonly NativeUiDriver Driver;
        internal readonly List<ImageRegion> Frames = [];
        private readonly FakeTimeProvider _clock = new();
        private readonly CaptureFrameSource _source;
        internal int Escapes;
        internal int Classifications;
        internal bool FailClassification, OtherSession;

        internal RecordedHandbook()
        {
            Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
            Assert.False(Pixels.Empty());
            _source = new(_clock);
            var ocr = new EmptyOcr();
            Driver = new(new NativeUiDriverIo
            {
                Capture = () =>
                {
                    _clock.Advance(TimeSpan.FromMilliseconds(1));
                    var frame = new ImageRegion(Pixels.Clone(), 0, 0,
                        drawingBoard: NullMaskWindowDrawingBoard.Instance) { FrameStamp = OtherSession ? new CaptureFrameSource(_clock).Next() : _source.Next() };
                    Frames.Add(frame);
                    return frame;
                },
                Focus = () => { },
                ReadScene = _ =>
                {
                    Classifications++;
                    if (FailClassification) throw new IOException("classification failed");
                    _clock.Advance(TimeSpan.FromMilliseconds(2600));
                    return new(1) { Handbook = true };
                },
                Ocr = () => ocr,
                Texts = () => new("地脉异常", "点击任意位置关闭"),
                Click = (_, _, _) => throw new InvalidOperationException("Unexpected click"),
                OtherAction = (action, _, admit) =>
                {
                    admit();
                    Assert.Equal(UiAction.Escape, action);
                    Escapes++;
                    return true;
                },
                Delay = (milliseconds, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    _clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                    return Task.CompletedTask;
                },
                Clock = _clock,
                BeginExclusive = () => new Noop()
            });
        }

        public void Dispose() { Driver.Dispose(); Pixels.Dispose(); }
    }

    private sealed class Noop : IDisposable { public void Dispose() { } }
    private sealed class EmptyOcr : IOcrService
    {
        public string Ocr(Mat mat) => "";
        public string OcrWithoutDetector(Mat mat) => "";
        public OcrResult OcrResult(Mat mat) => new([]);
    }
}
