using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.View.Drawable;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;
using Fischless.WindowsInput;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DomainTipNativeDriverTests
{
    [Fact]
    public async Task DismissRechecksAfterFocusAndClicksOnlyTheNewClientRectangle()
    {
        using var fixture = new DomainTipNativeFixture();
        var observed = fixture.Driver.Capture();
        Assert.True(observed.CanDismissDomainTip);
        Assert.False(observed.MainReady);
        Assert.False(observed.CanEscape);
        fixture.OnFocus = () => fixture.FooterOffset = 30;
        fixture.Events.Clear();

        Assert.True(await fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, default));

        var clicked = Assert.Single(fixture.Clicks);
        Assert.InRange(clicked.Bounds.X, 907, 909);
        Assert.True(clicked.Source.IsAfter(observed.SourceStamp));
        Assert.Equal(new[] { "focus", "capture", "read", "ocr-title", "ocr-footer", "click" }, fixture.Events);
        Assert.All(fixture.Frames, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public void IncompleteFakeOrNativeConstructionCannotAcquireLiveDependencies()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Throws<ArgumentNullException>(() => new NativeUiDriver(new NativeUiDriverIo()));
        Assert.Throws<InvalidOperationException>(() => new NativeUiDriver());
    }

    [Theory]
    [InlineData("disappeared")]
    [InlineData("title-only")]
    [InlineData("footer-only")]
    [InlineData("prompt")]
    [InlineData("revive")]
    [InlineData("defeat")]
    [InlineData("map")]
    [InlineData("party")]
    [InlineData("talk")]
    [InlineData("closable")]
    [InlineData("cannon")]
    [InlineData("handbook")]
    [InlineData("crafting")]
    [InlineData("menu")]
    [InlineData("exit-door")]
    public async Task PreInputSceneChangeNeverClicksABackgroundFooter(string change)
    {
        using var fixture = new DomainTipNativeFixture();
        var observed = fixture.Driver.Capture();
        fixture.OnFocus = () =>
        {
            fixture.Title = change is not ("disappeared" or "footer-only");
            fixture.Footer = change is not ("disappeared" or "title-only");
            fixture.Scene = Scene(change);
        };
        Assert.False(await fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, default));
        Assert.Empty(fixture.Clicks);
        Assert.Empty(fixture.Actions);
        Assert.All(fixture.Frames, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("prompt")]
    [InlineData("revive")]
    [InlineData("map")]
    [InlineData("party")]
    [InlineData("menu")]
    [InlineData("closable")]
    public void KnownPageRetainsItsExistingEscapePermissionDespiteBackgroundTipWords(string page)
    {
        using var fixture = new DomainTipNativeFixture { Scene = Scene(page) };
        var snapshot = fixture.Driver.Capture();
        Assert.True(snapshot.DomainTip.IsCandidate);
        Assert.False(snapshot.CanDismissDomainTip);
        Assert.True(snapshot.CanEscape);
        if (page == "map") Assert.True(snapshot.MapReady);
    }

    [Theory]
    [InlineData("repeat")]
    [InlineData("foreign")]
    [InlineData("unknown")]
    [InlineData("current-stale")]
    [InlineData("observed-stale")]
    [InlineData("old-fence")]
    public async Task OriginalSourceIdentityAndInputFenceRemainRequired(string fault)
    {
        using var fixture = new DomainTipNativeFixture();
        var observed = fixture.Driver.Capture();
        switch (fault)
        {
            case "repeat": fixture.SourceOverride = _ => observed.SourceStamp; break;
            case "foreign": fixture.SourceOverride = _ => new CaptureFrameSource(fixture.Clock).Next(); break;
            case "unknown": fixture.SourceOverride = _ => default; break;
            case "current-stale": fixture.AfterCapture = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "observed-stale": fixture.OnFocus = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "old-fence":
                fixture.Driver.MarkInputCompleted(observed);
                fixture.SourceOverride = _ => fixture.Producer.Next(observed.SourceStamp.CapturedTimestamp);
                break;
        }
        Assert.False(await fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, default));
        Assert.Empty(fixture.Clicks);
    }

    [Fact]
    public async Task ObservedEvidenceCannotChooseItsOwnFrozenClock()
    {
        using var fixture = new DomainTipNativeFixture();
        var observed = fixture.Driver.Capture();
        var frozenClock = new FakeTimeProvider();
        frozenClock.Advance(TimeSpan.FromMilliseconds(1));
        observed = observed.WithSource(observed.SourceStamp, frozenClock, UiSnapshot.RecoveryMaximumAge);
        fixture.OnFocus = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(await fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, default));
        Assert.True(observed.HasUsableEvidence, "the driver's clock, not a caller-selected frozen clock, must veto");
        Assert.Empty(fixture.Clicks);
    }

    [Theory]
    [InlineData("focus", false)]
    [InlineData("capture", false)]
    [InlineData("ocr", false)]
    [InlineData("focus", true)]
    [InlineData("capture", true)]
    [InlineData("ocr", true)]
    public async Task NativeBoundaryDelayCannotOutliveTheOriginalOperation(string phase, bool cancel)
    {
        using var fixture = new DomainTipNativeFixture();
        using var cancellation = new CancellationTokenSource();
        var observed = fixture.Driver.Capture();
        var interrupted = false;
        void Interrupt()
        {
            if (interrupted) return;
            interrupted = true;
            if (cancel) cancellation.Cancel();
            else fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        }
        if (phase == "focus") fixture.OnFocus = Interrupt;
        if (phase == "capture") fixture.AfterCapture = Interrupt;
        if (phase == "ocr") fixture.OnOcr = Interrupt;
        var error = await Record.ExceptionAsync(() => UiOperation.RunAsync("domain-tip-deadline", TimeSpan.FromSeconds(20),
            cancellation.Token, operation => fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, operation.Token), clock: fixture.Clock));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<TimeoutException>(error);
        Assert.True(interrupted);
        Assert.Empty(fixture.Clicks);
        Assert.All(fixture.Frames, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task PreInputDiagnosticsCannotKeepAnExpiredSourceFresh()
    {
        using var fixture = new DomainTipNativeFixture();
        var observed = fixture.Driver.Capture();
        fixture.OnFocus = () => fixture.Scene = fixture.Scene with { InDomain = false };
        var logger = new DelayedStateLog(fixture.Clock);
        var applied = await UiOperation.RunAsync("domain-tip-log", TimeSpan.FromSeconds(20), default, operation =>
        {
            operation.Observe(observed, UiTarget.Overworld);
            return fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, operation.Token);
        }, logger, fixture.Clock);
        Assert.True(logger.Delayed);
        Assert.False(applied);
        Assert.Empty(fixture.Clicks);
    }

    [Fact]
    public async Task ACompletedClickFenceExcludesFramesCapturedBeforeItsReturn()
    {
        using var fixture = new DomainTipNativeFixture();
        var observed = fixture.Driver.Capture();
        fixture.OnClick = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(40));
        Assert.True(await fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, default));
        var clicked = Assert.Single(fixture.Clicks);
        fixture.SourceOverride = _ => fixture.Producer.Next(clicked.Source.CapturedTimestamp + fixture.Clock.TimestampFrequency / 1000);
        Assert.False(fixture.Driver.Capture().HasUsableEvidence);
        fixture.SourceOverride = null;
        Assert.True(fixture.Driver.Capture().HasUsableEvidence);
    }

    [Fact]
    public async Task ClickFailurePropagatesWithoutFabricatingACompletedInputFence()
    {
        using var fixture = new DomainTipNativeFixture();
        var observed = fixture.Driver.Capture();
        var failure = new IOException("partial click");
        fixture.OnClick = () => throw failure;
        Assert.Same(failure, await Record.ExceptionAsync(() => fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, default)));
        var attempted = Assert.Single(fixture.Clicks);
        fixture.SourceOverride = _ => attempted.Source;
        Assert.True(fixture.Driver.Capture().HasUsableEvidence);
        Assert.All(fixture.Frames, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("move", 2100)]
    [InlineData("down", 2100)]
    [InlineData("move", 21000)]
    [InlineData("down", 21000)]
    public async Task ClickHelperRechecksAtEachRealDispatcherBoundaryAfterTransportPreparation(string delayedStage, int delay)
    {
        using var fixture = new DomainTipNativeFixture();
        var observed = fixture.Driver.Capture();
        fixture.BeforeTransport = stage =>
        {
            if (stage == delayedStage) fixture.Clock.Advance(TimeSpan.FromMilliseconds(delay));
        };
        var error = await Record.ExceptionAsync(() => UiOperation.RunAsync("domain-tip-transport", TimeSpan.FromSeconds(20),
            default, operation => fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, operation.Token), clock: fixture.Clock));
        if (delay < 20000)
            Assert.Contains("source expired", Assert.IsType<InvalidOperationException>(error).Message);
        else Assert.IsType<TimeoutException>(error);
        Assert.DoesNotContain(fixture.NativeStages, stage => stage == delayedStage || stage == "down");
        Assert.Empty(fixture.Clicks);
        if (delayedStage == "down") Assert.Equal(new[] { "move" }, fixture.NativeStages);
        else Assert.Empty(fixture.NativeStages);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnAdmittedMouseDownIsReleasedEvenAfterDeadlineOrCancellation(bool cancel)
    {
        using var fixture = new DomainTipNativeFixture();
        using var cancellation = new CancellationTokenSource();
        var observed = fixture.Driver.Capture();
        fixture.OnClick = () =>
        {
            if (cancel) cancellation.Cancel();
            else fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        };
        var error = await Record.ExceptionAsync(() => UiOperation.RunAsync("domain-tip-cleanup", TimeSpan.FromSeconds(20),
            cancellation.Token, operation => fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, operation.Token), clock: fixture.Clock));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<TimeoutException>(error);
        Assert.Equal(new[] { "move", "down", "up" }, fixture.NativeStages);
        Assert.Single(fixture.Clicks);
    }

    [Fact]
    public async Task ClickAndCleanupFailuresAreBothPreservedWithoutRetry()
    {
        using var fixture = new DomainTipNativeFixture();
        var observed = fixture.Driver.Capture();
        var original = new IOException("unknown down result");
        var cleanup = new IOException("unknown release result");
        fixture.OnClick = () => throw original;
        fixture.BeforeTransport = stage => { if (stage == "up") throw cleanup; };
        var failure = await Assert.ThrowsAsync<AggregateException>(() => fixture.Driver.ActAsync(UiAction.DismissDomainTip, observed, default));
        Assert.Equal(new Exception[] { original, cleanup }, failure.InnerExceptions);
        Assert.Equal(new[] { "move", "down" }, fixture.NativeStages);
        Assert.Single(fixture.Clicks);
    }

    private static UiSnapshot Scene(string name) => name switch
    {
        "prompt" => new(1) { Prompt = true, BlackConfirm = true },
        "revive" => new(1) { Revive = true },
        "defeat" => new(1) { FullPartyDefeat = true },
        "map" => new(1) { BigMap = true },
        "party" => new(1) { Party = true },
        "talk" => new(1) { Talk = true },
        "closable" => new(1) { Closable = true },
        "cannon" => new(1) { Cannon = true },
        "handbook" => new(1) { Handbook = true },
        "crafting" => new(1) { Crafting = true },
        "menu" => new(1) { MenuBack = true },
        "exit-door" => new(1) { ExitDoor = true },
        _ => new(1) { MainHud = true }
    };

    private sealed class DelayedStateLog(FakeTimeProvider clock) : ILogger
    {
        internal bool Delayed;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (!Delayed && formatter(state, error).Contains("phase=pre-input", StringComparison.Ordinal))
            { Delayed = true; clock.Advance(TimeSpan.FromSeconds(2.5)); }
        }
    }
}

// Every side effect is explicitly supplied; the real Read/Act policies are not copied here.
internal sealed class DomainTipNativeFixture : IDisposable
{
    internal readonly FakeTimeProvider Clock = new();
    internal readonly CaptureFrameSource Producer;
    internal readonly NativeUiDriver Driver;
    internal readonly List<ImageRegion> Frames = new();
    internal readonly List<string> Events = new();
    internal readonly List<(Rect Bounds, CaptureFrameStamp Source)> Clicks = new();
    internal readonly List<UiAction> Actions = new();
    internal readonly List<string> NativeStages = new();
    internal UiSnapshot Scene = new(1) { MainHud = true, InDomain = true };
    internal bool Title = true, Footer = true;
    internal int FooterOffset;
    internal Action? OnFocus, BeforeCapture, AfterCapture, OnOcr, OnClick;
    internal Func<CaptureFrameStamp, CaptureFrameStamp>? SourceOverride;
    internal Func<UiAction, bool>? OnAction;
    internal Action<string>? BeforeTransport;
    private readonly IOcrService _ocr;

    internal DomainTipNativeFixture()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Producer = new(Clock);
        _ocr = new FixtureOcr(this);
        Driver = new(new NativeUiDriverIo
        {
            Capture = Capture,
            Focus = () => { Events.Add("focus"); OnFocus?.Invoke(); },
            ReadScene = _ => { Events.Add("read"); return Scene; },
            Ocr = () => _ocr,
            Texts = () => DomainTipUiReaderTests.Texts,
            Click = Click,
            OtherAction = (action, _) => { Actions.Add(action); return OnAction?.Invoke(action) ?? throw new InvalidOperationException("No fake action was supplied."); },
            Delay = (milliseconds, ct) => { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); return Task.CompletedTask; },
            Clock = Clock,
            BeginExclusive = () => new NoopLease()
        });
    }

    private void Click(ImageRegion image, Rect bounds, Action admission)
    {
        Events.Add("click");
        var stage = "";
        var dispatcher = new WindowsInputMessageDispatcher(() => BeforeTransport?.Invoke(stage), inputs =>
        {
            NativeStages.Add(stage);
            if (stage == "down") { Clicks.Add((bounds, image.FrameStamp)); OnClick?.Invoke(); }
            return (uint)inputs.Length;
        }, () => 0);
        void Dispatch(string next) { stage = next; dispatcher.DispatchInput(new User32.INPUT[1]); }
        DomainTipClick.Run(admission, () => Dispatch("move"), () => Dispatch("down"), () => Dispatch("up"),
            milliseconds => Clock.Advance(TimeSpan.FromMilliseconds(milliseconds)));
    }

    private ImageRegion Capture()
    {
        Events.Add("capture");
        BeforeCapture?.Invoke();
        Clock.Advance(TimeSpan.FromMilliseconds(1));
        var source = Producer.Next();
        var image = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0,
            drawContent: new DrawContent()) { FrameStamp = SourceOverride?.Invoke(source) ?? source };
        Frames.Add(image);
        try { AfterCapture?.Invoke(); return image; }
        catch { image.Dispose(); throw; }
    }

    private sealed class FixtureOcr(DomainTipNativeFixture fixture) : IOcrService
    {
        public string Ocr(Mat image) => throw new InvalidOperationException("Detection only.");
        public string OcrWithoutDetector(Mat image) => throw new InvalidOperationException("Detection only.");
        public OcrResult OcrResult(Mat image)
        {
            var title = image.Height == 85;
            fixture.Events.Add(title ? "ocr-title" : "ocr-footer");
            fixture.OnOcr?.Invoke();
            if (!(title ? fixture.Title : fixture.Footer)) return new([]);
            return title
                ? new([new(new(new Point2f(114, 45), new Size2f(132, 34), 0), DomainTipUiReaderTests.Texts.Title, 1)])
                : new([new(new(new Point2f(412 + fixture.FooterOffset, 25), new Size2f(168, 22), 0), DomainTipUiReaderTests.Texts.Close, 1)]);
        }
    }

    private sealed class NoopLease : IDisposable { public void Dispose() { } }

    public void Dispose()
    {
        Driver.Dispose();
        foreach (var frame in Frames) if (!frame.SrcMat.IsDisposed) frame.Dispose();
    }
}
