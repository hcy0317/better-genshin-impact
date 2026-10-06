using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Core.Mask;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class PartyCloseRecoveryTests
{
    [Fact]
    public async Task NativePartyRecoveryClicksTheMatchedXAfterEscapeAndWaitsForMain()
    {
        using var fixture = new Fixture();
        var result = await UiRecovery.ToMainAsync(fixture.Driver, default, clock: fixture.Clock);
        Assert.True(result.MainReady);
        Assert.Equal(1, fixture.Escapes);
        Assert.Equal(1, fixture.Clicks);
        Assert.True(fixture.MainFrames >= 2);
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("foreign")]
    [InlineData("stale")]
    [InlineData("no-button")]
    public async Task NativeCloseRejectsChangedOrUnusableEvidence(string fault)
    {
        using var fixture = new Fixture();
        var before = fixture.Driver.Capture();
        if (fault == "prompt") fixture.Scene = fixture.Scene with { Prompt = true };
        if (fault == "foreign") fixture.Producer = new(fixture.Clock);
        if (fault == "stale") fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        if (fault == "no-button") fixture.Button = false;
        Assert.False(await fixture.Driver.ActAsync(UiAction.CloseParty, before, default));
        Assert.Equal(0, fixture.Clicks);
    }

    [Fact]
    public async Task NativeCloseChecksFreshnessAgainAtTheClickBoundary()
    {
        using var fixture = new Fixture();
        var before = fixture.Driver.Capture();
        fixture.BeforeClick = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Driver.ActAsync(UiAction.CloseParty, before, default));
        Assert.Equal(0, fixture.Clicks);
    }

    [Fact]
    public async Task NewCaptureSessionCannotInheritTheEarlierEscapeFallback()
    {
        using var fixture = new Fixture();
        fixture.AfterEscape = () => { if (fixture.Escapes == 1) fixture.Producer = new(fixture.Clock); };
        await Assert.ThrowsAsync<TimeoutException>(() => UiRecovery.ToMainAsync(fixture.Driver, default, clock: fixture.Clock));
        Assert.Equal(1, fixture.Escapes);
        Assert.Equal(0, fixture.Clicks);
    }

    private sealed class Fixture : IDisposable
    {
        internal readonly FakeTimeProvider Clock = new();
        internal CaptureFrameSource Producer;
        internal readonly NativeUiDriver Driver;
        internal UiSnapshot Scene = new(1) { Party = true, Closable = true };
        internal bool Button = true;
        internal int Escapes, Clicks, MainFrames;
        internal Action? BeforeClick;
        internal Action? AfterEscape;
        private readonly Mat _template;

        internal Fixture()
        {
            Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
            Producer = new(Clock);
            _template = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "GameTask", "QuickTeleport", "Assets", "1920x1080", "MapCloseButton.png"));
            Assert.False(_template.Empty());
            Driver = new(new NativeUiDriverIo
            {
                Capture = () =>
                {
                    Clock.Advance(TimeSpan.FromMilliseconds(1));
                    var pixels = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
                    if (Button && Scene.Party)
                    {
                        using var roi = new Mat(pixels, new Rect(1820, 24, _template.Width, _template.Height));
                        _template.CopyTo(roi);
                    }
                    if (Scene.MainHud) MainFrames++;
                    return new ImageRegion(pixels, 0, 0, drawingBoard: NullMaskWindowDrawingBoard.Instance) { FrameStamp = Producer.Next() };
                },
                Focus = () => { }, ReadScene = _ => Scene, Ocr = () => new EmptyOcr(), Texts = () => new("", ""),
                Click = (_, bounds, admission) =>
                {
                    BeforeClick?.Invoke(); admission();
                    Assert.InRange(bounds.X, 1813, 1871);
                    Clicks++; Scene = new(1) { MainHud = true };
                },
                OtherAction = (action, _, admission) =>
                {
                    admission(); Assert.Equal(UiAction.Escape, action); Escapes++; AfterEscape?.Invoke(); return true;
                },
                Delay = (ms, ct) => { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
                Clock = Clock, BeginExclusive = () => new Lease()
            });
        }
        public void Dispose() { Driver.Dispose(); _template.Dispose(); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
        private sealed class EmptyOcr : IOcrService
        {
            public string Ocr(Mat image) => "";
            public string OcrWithoutDetector(Mat image) => "";
            public OcrResult OcrResult(Mat image) => new([]);
        }
    }
}
