using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Time.Testing;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.Core.Recognition.OCR;
using System.Reflection;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Helpers;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class CurrentPlayerUidTaskTests
{
    [Fact]
    public async Task StartReadsCurrentOwnMenuWhenFooterIsUnavailableAndPublishesOnlyAfterWorld()
    {
        var io = new FakeIo();
        var result = await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None);
        Assert.Equal(123456789, result);
        Assert.Equal(1, io.MenuInputs);
        Assert.True(io.WorldCapturesAfterReturn >= 2);
        Assert.False(io.Menu);
    }

    [Theory]
    [InlineData("UID 123456789", 123456789)]
    [InlineData("UID 1234567890", 1234567890)]
    public async Task LegalFooterKeepsFastPathWithoutMenu(string text, int expected)
    {
        var io = new FakeIo { Footer = text };
        Assert.Equal(expected, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(0, io.MenuInputs);
        Assert.Equal(0, io.EscapeInputs);
    }

    [Theory]
    [InlineData("wrong-page")]
    [InlineData("missing-card")]
    [InlineData("missing-label")]
    [InlineData("conflicting")]
    [InlineData("overflow")]
    [InlineData("no-uid")]
    [InlineData("unrelated-digits")]
    [InlineData("wrong-session")]
    public async Task MenuIdentityMustBeCurrentUniqueAndOwned(string defect)
    {
        var io = new FakeIo { Defect = defect };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(1, io.MenuInputs);
        Assert.False(io.Menu);
    }

    [Fact]
    public async Task SplitLabelAndDigitsUseSameFrameGeometry()
    {
        var io = new FakeIo { Defect = "split" };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.True(io.WorldCapturesAfterReturn >= 2);
    }

    [Fact]
    public async Task FailedWorldReturnNeverPublishesAnAlreadyReadCandidate()
    {
        var io = new FakeIo { BlockReturn = true };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(1, io.MenuInputs);
        Assert.True(io.Clock.GetUtcNow() - io.Started <= TimeSpan.FromSeconds(5.3));
    }

    [Fact]
    public async Task MenuNeverReadyUsesOneOpenAndTheSingleTotalBudget()
    {
        var io = new FakeIo { BlockMenu = true };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(1, io.MenuInputs);
        Assert.True(io.Clock.GetUtcNow() - io.Started <= TimeSpan.FromSeconds(5.3));
    }

    [Fact]
    public async Task CancellationAfterReadPropagatesAndDoesNotInputForCleanup()
    {
        using var cancelled = new CancellationTokenSource();
        var io = new FakeIo { AfterMenuRead = cancelled.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CurrentPlayerUidTask(io, io.Clock).Start(cancelled.Token));
        Assert.Equal(1, io.MenuInputs);
        Assert.Equal(0, io.EscapeInputs);
    }

    [Fact]
    public async Task CancelledParentNeverCapturesOrOpensMenu()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var io = new FakeIo();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CurrentPlayerUidTask(io, io.Clock).Start(cancelled.Token));
        Assert.Equal(0, io.Captures);
        Assert.Equal(0, io.MenuInputs);
    }

    [Fact]
    public async Task ParentRemainingBudgetIsNotRestartedAtFiveSeconds()
    {
        var io = new FakeIo();
        using var parent = UiOperation.Begin("uid-parent", TimeSpan.FromMilliseconds(300), logger: NullLogger.Instance, clock: io.Clock);
        try { Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(parent.Token)); }
        catch (OperationCanceledException) { }
        catch (TimeoutException) { }
        Assert.Equal(0, io.MenuInputs);
        Assert.True(io.Clock.GetUtcNow() - io.Started <= TimeSpan.FromMilliseconds(300));
        Assert.Equal(0, io.EscapeInputs);
    }

    [Fact]
    public async Task AlreadyOwnMenuDoesNotOpenItAgainButStillReturnsWorld()
    {
        var io = new FakeIo { Menu = true };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(0, io.MenuInputs);
        Assert.True(io.WorldCapturesAfterReturn >= 2);
    }

    [Fact]
    public async Task UnavailableCapturePreservesFailureZeroWithoutUiInput()
    {
        var io = new FakeIo { Defect = "capture-failed" };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(0, io.MenuInputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NormalTerminalEndPropagatesEvenWithoutCancelledToken(bool afterMenu)
    {
        var io = new FakeIo();
        if (afterMenu) io.AfterMenuRead = () => throw new NormalEndException("synthetic normal end");
        else io.Defect = "normal-end";
        await Assert.ThrowsAsync<NormalEndException>(() => new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(afterMenu ? 1 : 0, io.MenuInputs);
        Assert.Equal(0, io.EscapeInputs);
    }

    [Fact]
    public async Task PreparedServicesResolveOnceAtCompositionAndNeverDuringStart()
    {
        var lookups = 0;
        var textLookups = 0;
        var sessions = 0;
        var service = DispatchProxy.Create<IOcrService, ForbiddenOcrProxy>();
        var io = new FakeIo { Footer = "UID 123456789" };
        var boundaries = new NativeUiDriverIo
        {
            Clock = io.Clock,
            Ocr = () => throw new InvalidOperationException("Uncaptured OCR lookup"),
            Texts = () => { textLookups++; return new("synthetic domain", "synthetic close"); }
        };
        var task = CurrentPlayerUidTask.CreatePrepared(boundaries, () => { lookups++; return service; }, NullLogger.Instance,
            (prepared, ocr) =>
            {
                sessions++;
                Assert.Same(service, ocr);
                Assert.Same(service, prepared.Ocr());
                Assert.Same(service, prepared.Ocr());
                prepared.Texts();
                prepared.Texts();
                return io;
            });
        Assert.Equal(1, lookups);
        Assert.Equal(1, textLookups);
        Assert.Equal(0, sessions);
        Assert.Equal(123456789, await task.Start(CancellationToken.None));
        Assert.Equal(1, lookups);
        Assert.Equal(1, textLookups);
        Assert.Equal(1, sessions);
    }

    public class ForbiddenOcrProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? method, object?[]? args) =>
            throw new InvalidOperationException("The factory contract test must not execute OCR");
    }

    [Fact]
    public async Task RealMenuSceneLatencyStillReturnsUidAfterTwoFreshWorldFramesWithinOriginalFiveSeconds()
    {
        var io = new FakeIo { MeasuredLatency = true };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(1, io.MenuInputs);
        Assert.Equal(1, io.EscapeInputs);
        Assert.True(io.WorldCapturesAfterReturn >= 2);
        Assert.True(io.Clock.GetUtcNow() - io.Started < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("late-menu")]
    [InlineData("no-world")]
    [InlineData("foreign-world")]
    [InlineData("replayed-world")]
    public async Task MeasuredLatencyNeverPublishesWithoutTimelyTwoFreshOwnedWorldFrames(string failure)
    {
        var io = new FakeIo { MeasuredLatency = true, ExtraOpeningFrames = failure == "late-menu" ? 2 : 0,
            BlockReturn = failure == "no-world", ForeignWorld = failure == "foreign-world", ReplayWorld = failure == "replayed-world" };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(1, io.MenuInputs);
        Assert.InRange(io.EscapeInputs, 0, 1);
    }

    [Fact]
    public async Task AlreadyMenuWithRealSceneStartStampClosesAndReturnsWithinOriginalBudget()
    {
        var io = new FakeIo { MeasuredLatency = true, Menu = true };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(0, io.MenuInputs);
        Assert.Equal(1, io.EscapeInputs);
        Assert.Equal(1, io.MenuOcrReads);
        Assert.False(io.Menu);
        Assert.True(io.WorldCapturesAfterReturn >= 2);
        Assert.True(io.Clock.GetUtcNow() - io.Started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AcceptedMenuCandidateSurvivesTwoFreshSevenHundredMillisecondWorldFrames()
    {
        var io = new FakeIo { MeasuredLatency = true, WorldCaptureMs = 700 };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(1, io.MenuInputs);
        Assert.Equal(1, io.EscapeInputs);
        Assert.True(io.WorldCapturesAfterReturn >= 2);
        Assert.True(io.Clock.GetUtcNow() - io.Started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExpiredSeedIntentUsesFreshRemainingRecoveryWithoutBlindEscapeOrLeavingMenu()
    {
        var io = new FakeIo { MeasuredLatency = true, Menu = true, ExtraMenuReadMs = 300 };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(0, io.MenuInputs);
        Assert.Equal(1, io.EscapeInputs);
        Assert.Equal(1, io.MenuOcrReads);
        Assert.False(io.Menu);
        Assert.True(io.WorldCapturesAfterReturn >= 2);
        Assert.True(io.Clock.GetUtcNow() - io.Started < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExpiredSeedCleanupSentWithoutWorldConsumesOnlyOneEscapeAuthorization()
    {
        var io = new FakeIo { MeasuredLatency = true, Menu = true, ExtraMenuReadMs = 300, BlockReturn = true };
        Assert.Equal(0, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(0, io.MenuInputs);
        Assert.Equal(1, io.EscapeInputs);
    }

    [Fact]
    public async Task ReadonlySceneWorkDoesNotAddResponseSleepsBetweenSevenFreshCaptures()
    {
        var io = new FakeIo { MeasuredLatency = true, ExtraNonconfirmingWorldFrames = 1 };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Equal(7, io.Captures);
        Assert.Equal(1, io.MenuInputs);
        Assert.Equal(1, io.EscapeInputs);
        Assert.Equal(2, io.ConfirmedWorldCaptures);
        Assert.True(io.Clock.GetUtcNow() - io.Started < TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PotentialOwnedEscapeKeepsResponseAfterReadReturnWithoutAppliedReceipt(bool noNativeSubmit)
    {
        var io = new FakeIo { MeasuredLatency = true, HideOwnedEscapeReceipt = true, DropOwnedEscapeSubmit = noNativeSubmit };
        var result = await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None);
        Assert.Equal(noNativeSubmit ? 0 : 123456789, result);
        Assert.NotEmpty(io.PostInputCaptureGapsMs);
        Assert.All(io.PostInputCaptureGapsMs, gap => Assert.True(gap >= 60, $"post-input capture gap {gap:F3}ms"));
        Assert.InRange(io.EscapeInputs, 0, 1);
    }

    [Fact]
    public async Task PotentialFallbackEscapeKeepsResponseAfterActReturnEvenWhenNotApplied()
    {
        var io = new FakeIo { MeasuredLatency = true, Menu = true, ExtraMenuReadMs = 300, HideFallbackActReceipt = true };
        Assert.Equal(123456789, await new CurrentPlayerUidTask(io, io.Clock).Start(CancellationToken.None));
        Assert.Single(io.PostInputCaptureGapsMs);
        Assert.True(io.PostInputCaptureGapsMs[0] >= 60);
        Assert.Equal(1, io.EscapeInputs);
    }

    [Fact]
    [Trait("UidNativeStage", "Baseline")]
    public void PreparedNativeMenuOcrRegionsPassTheProductionOwnCardDecoder() =>
        PreparedNativeUidScene.AssertActualMenuDecoderPremise();

    [Fact]
    [Trait("UidNativeStage", "Baseline")]
    public async Task PreparedNativeSessionPublishesOwnedMenuUidAfterTwoFreshWorldFrames()
    {
        PreparedNativeUidScene.AssertActualMenuDecoderPremise();
        using var scene = new PreparedNativeUidScene();
        var result = await scene.CreateTask().Start(CancellationToken.None);
        scene.AssertOwnedMenuRoundTripPremises();
        Assert.True(result == 123456789, scene.FailureSummary(result));
        Assert.True(scene.Elapsed < CurrentPlayerUidTask.TotalBudget, scene.FailureSummary(result));
        Assert.True(scene.WorldFramesAfterEscape >= 2);
    }

    [Fact]
    [Trait("UidNativeStage", "Baseline")]
    public async Task PreparedNativeSessionKeepsWorldHudDomainTipOverlayAsARejection()
    {
        PreparedNativeUidScene.AssertActualMenuDecoderPremise();
        using var scene = new PreparedNativeUidScene { WorldDomainTip = true };
        var result = await scene.CreateTask().Start(CancellationToken.None);
        Assert.Equal(1, scene.MenuUidReads);
        Assert.Equal(1, scene.EscapeInputs);
        Assert.True(scene.WorldDomainTipReads > 0);
        Assert.True(scene.WorldFramesAfterEscape >= 2);
        Assert.Equal(0, scene.UnexpectedInputs);
        Assert.Equal(0, result);
    }

    [Fact]
    [Trait("UidNativeStage", "Baseline")]
    public async Task PreparedNativeSessionKeepsInitialOpenMenuControlProofFull()
    {
        using var scene = new PreparedNativeUidScene { InitialWorldWithoutControl = true };
        var result = await scene.CreateTask().Start(CancellationToken.None);
        Assert.Single(scene.Projections);
        Assert.True(scene.Projections[0].InspectWorld);
        Assert.True(scene.InitialSourceWasSystemFresh);
        Assert.Equal(0, scene.EscapeInputs);
        Assert.Equal(0, scene.UnexpectedInputs);
        Assert.Equal(0, result);
    }

    [Fact]
    [Trait("UidNativeStage", "PerformanceCounter")]
    public async Task PreparedNativeSessionPublishesWithinFiveSecondsWithoutRepeatingRichWorldProjection()
    {
        PreparedNativeUidScene.AssertActualMenuDecoderPremise();
        using var scene = new PreparedNativeUidScene { RichWorldCost = TimeSpan.FromMilliseconds(1500) };
        var result = await scene.CreateTask().Start(CancellationToken.None);
        scene.AssertOwnedMenuRoundTripPremises();
        Assert.True(scene.WorldFramesAfterEscape >= 2);
        Assert.All(scene.Projections.Where(p => !p.Menu), projection =>
        {
            Assert.True(projection.PlainWorldReady);
            Assert.True(projection.SourceFreshWhenProjected);
        });
        // These inputs are valid under the original source/response contracts.  The
        // failing business assertion must be publication, not a missing adapter/App.
        Assert.True(result == 123456789, scene.FailureSummary(result));
        Assert.True(scene.Elapsed < CurrentPlayerUidTask.TotalBudget, scene.FailureSummary(result));
    }

    private sealed class PreparedNativeUidScene : IDisposable
    {
        internal sealed record Projection(CaptureFrameStamp Source, bool Menu, bool InspectWorld,
            bool PlainWorldReady, bool SourceFreshWhenProjected);
        private readonly Guid _session = Guid.NewGuid();
        private long _sequence;
        private bool _menu = true;
        private long? _escapeReturned;
        private readonly PreparedUidOcr _ocr;
        private readonly DateTimeOffset _started;
        private readonly PreparedUidLogger _logger = new();
        internal PreparedUidClock Clock { get; } = new();
        internal List<Projection> Projections { get; } = new();
        internal List<double> PostEscapeCaptureGaps { get; } = new();
        internal TimeSpan RichWorldCost { get; init; }
        internal bool WorldDomainTip { get; init; }
        internal bool InitialWorldWithoutControl { get; init; }
        internal bool InitialSourceWasSystemFresh { get; private set; }
        internal int EscapeInputs { get; private set; }
        internal int UnexpectedInputs { get; private set; }
        internal int WorldFramesAfterEscape { get; private set; }
        internal int MenuUidReads { get; private set; }
        internal int MenuDomainTipReads { get; private set; }
        internal int WorldDomainTipReads { get; private set; }
        internal int ServiceResolutions { get; private set; }
        internal TimeSpan Elapsed => Clock.GetUtcNow() - _started;
        internal PreparedNativeUidScene()
        {
            ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
            Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
            _started = Clock.GetUtcNow();
            _ocr = new PreparedUidOcr(this);
        }
        internal CurrentPlayerUidTask CreateTask()
        {
            var prepared = new NativeUiDriverIo
            {
                Capture = Capture,
                Focus = () => { },
                ReadScene = _ => throw new InvalidOperationException("The explicit prepared native reader must be used"),
                ReadOrdinaryWorld = _ => throw new InvalidOperationException("The explicit prepared ordinary-world reader must be used"),
                Ocr = () => throw new InvalidOperationException("Uncaptured OCR service lookup"),
                Texts = () => new("synthetic domain tip", "synthetic close"),
                Clock = Clock,
                NetworkAvailable = () => true,
                BeginExclusive = () => new PreparedUidLease(),
                Click = (_, _, _) => { UnexpectedInputs++; throw new InvalidOperationException("No click is authorized in this fixture"); },
                OtherAction = (action, image, admit) =>
                {
                    if (action != UiAction.Escape)
                    { UnexpectedInputs++; throw new InvalidOperationException("Only the injected owned Escape is authorized"); }
                    Assert.True(_menu);
                    Assert.True(image.FrameStamp.IsFresh(Clock, UiSnapshot.RecoveryMaximumAge));
                    admit();
                    EscapeInputs++;
                    _menu = false;
                    _escapeReturned = Clock.GetTimestamp();
                    return true;
                },
                Delay = (milliseconds, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                }
            };
            // No createSession override: the production NativeIo, NativeUiDriver,
            // UID geometry decoder, owned Escape and return transition all execute.
            return CurrentPlayerUidTask.CreatePrepared(prepared,
                () => { ServiceResolutions++; return _ocr; }, _logger,
                readScene: ReadNativeScene);
        }
        internal static void AssertActualMenuDecoderPremise()
        {
            using var probe = new PreparedNativeUidScene();
            using var image = probe.Capture();
            var scene = new UiSnapshot(image.FrameStamp.Sequence) { MenuBack = true }
                .WithSource(image.FrameStamp, probe.Clock, UiSnapshot.RecoveryMaximumAge);
            var regions = image.FindMulti(RecognitionObject.Ocr(95, 0, 675, 780), ocrService: probe._ocr);
            var text = regions.Select(region => new CurrentPlayerUidText(region.Text,
                region.X, region.Y, region.Width, region.Height)).ToArray();
            var frame = new CurrentPlayerUidFrame(scene, image.Width, image.Height, text);
            var decoded = CurrentPlayerUidTask.Decode(frame, true);
            var rectangles = string.Join(";", text.Select(value =>
                $"{value.X},{value.Y},{value.Width},{value.Height}"));
            Assert.True(decoded == 123456789,
                $"actualDecodePositive={decoded > 0}; regionCount={text.Length}; rects={rectangles}");
        }
        internal string FailureSummary(int result) =>
            $"resultPositive={result > 0}; elapsedMs={Elapsed.TotalMilliseconds:F1}; nowTimestamp={Clock.GetTimestamp()}; " +
            $"frequency={Clock.TimestampFrequency}; escapeReturned={_escapeReturned}; menuOcr={MenuUidReads}; " +
            $"escape={EscapeInputs}; worldCaptures={WorldFramesAfterEscape}; unexpectedInputs={UnexpectedInputs}; " +
            $"postInputCaptureGapsMs=[{string.Join(",", PostEscapeCaptureGaps.Select(gap => gap.ToString("F1")))}]; " +
            $"projections=[{string.Join(";", Projections.Select(projection =>
                $"source={projection.Source.Sequence},capturedTimestamp={projection.Source.CapturedTimestamp}," +
                $"menu={projection.Menu},rich={projection.InspectWorld}," +
                $"worldReady={projection.PlainWorldReady},fresh={projection.SourceFreshWhenProjected}"))}]; " +
            $"events=[{string.Join(" | ", _logger.Events)}]";
        private sealed class PreparedUidLogger : ILogger
        {
            internal List<string> Events { get; } = new();
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => new PreparedUidLease();
            public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
            public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                Events.Add(formatter(state, exception));
                if (exception != null) Events.Add(exception.GetType().Name + ": " + exception.Message);
            }
        }
        private ImageRegion Capture()
        {
            if (!InitialWorldWithoutControl) Clock.Advance(TimeSpan.FromMilliseconds(1));
            var menu = !InitialWorldWithoutControl && _menu;
            if (_escapeReturned is { } returned)
            {
                PostEscapeCaptureGaps.Add(Clock.GetElapsedTime(returned).TotalMilliseconds);
                WorldFramesAfterEscape++;
            }
            var source = new CaptureFrameStamp(_session, ++_sequence, Clock.GetTimestamp(), Clock.TimestampFrequency, Clock.GetUtcNow());
            return new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.All(menu ? 17 : 29)), 0, 0)
                { FrameStamp = source };
        }
        private UiSnapshot ReadNativeScene(ImageRegion image, bool inspectWorld, IOcrService service)
        {
            Assert.Same(_ocr, service);
            var menu = image.SrcMat.At<Vec3b>(0, 0).Item0 == 17;
            if (Projections.Count == 0)
                InitialSourceWasSystemFresh = image.FrameStamp.IsFresh(TimeProvider.System, UiSnapshot.RecoveryMaximumAge);
            if (menu) Clock.Advance(TimeSpan.FromMilliseconds(500));
            var snapshot = new UiSnapshot(image.FrameStamp.Sequence) { MenuBack = menu, MainHud = !menu }
                .WithSource(image.FrameStamp, Clock, UiSnapshot.RecoveryMaximumAge);
            if (!menu && inspectWorld)
            {
                snapshot = snapshot with { World = new UiWorldObservation(false, false, false)
                    { OrdinaryAvatarHud = true, Transformed = false, ControlObserved = !InitialWorldWithoutControl,
                        KeyboardBreakout = false } };
                if (_escapeReturned != null) Clock.Advance(RichWorldCost);
            }
            Projections.Add(new(image.FrameStamp, menu, inspectWorld, snapshot.MainReady,
                image.FrameStamp.IsFresh(Clock, UiSnapshot.RecoveryMaximumAge)));
            return snapshot;
        }
        internal void AssertOwnedMenuRoundTripPremises()
        {
            Assert.Equal(1, ServiceResolutions);
            Assert.Equal(1, MenuUidReads);
            Assert.Equal(1, EscapeInputs);
            Assert.Equal(0, UnexpectedInputs);
            Assert.True(Projections[0].Menu);
            Assert.True(Projections[0].InspectWorld);
            Assert.True(MenuDomainTipReads > 0);
            Assert.True(WorldDomainTipReads > 0);
            Assert.NotEmpty(PostEscapeCaptureGaps);
            Assert.True(PostEscapeCaptureGaps[0] >= 60);
            var sources = Projections.Select(p => p.Source).ToArray();
            Assert.All(sources, source => Assert.Equal(_session, source.SessionId));
            for (var i = 1; i < sources.Length; i++) Assert.True(sources[i].IsAfter(sources[i - 1]));
        }
        public void Dispose() { }
        private sealed class PreparedUidLease : IDisposable { public void Dispose() { } }
        private sealed class PreparedUidOcr(PreparedNativeUidScene scene) : IOcrService
        {
            public string Ocr(Mat mat) => OcrResult(mat).Text;
            public string OcrWithoutDetector(Mat mat) => OcrResult(mat).Text;
            public OcrResult OcrResult(Mat mat)
            {
                RecognitionExecutionScope.Token.ThrowIfCancellationRequested();
                var menu = mat.At<Vec3b>(0, 0).Item0 == 17;
                if (mat.Width == 675 && mat.Height == 780 && menu)
                {
                    scene.MenuUidReads++;
                    scene.Clock.Advance(TimeSpan.FromMilliseconds(300));
                    RecognitionExecutionScope.Token.ThrowIfCancellationRequested();
                    return new(new[]
                    {
                        Region("冒险等阶", 220, 170, 100, 24), Region("世界等级", 220, 250, 100, 24),
                        Region("生日", 220, 290, 50, 24), Region("队伍配置", 205, 440, 100, 24),
                        Region("UID 123456789", 20, 195, 182, 30)
                    });
                }
                var title = mat.Width == 1050 && mat.Height == 85;
                var close = mat.Width == 820 && mat.Height == 47;
                if (title || close)
                {
                    if (menu) scene.MenuDomainTipReads++; else scene.WorldDomainTipReads++;
                    scene.Clock.Advance(TimeSpan.FromMilliseconds(100));
                    RecognitionExecutionScope.Token.ThrowIfCancellationRequested();
                    if (!menu && scene.WorldDomainTip)
                        return new(new[] { Region(title ? "synthetic domain tip" : "synthetic close", 10, 10, 200, 20) });
                }
                return new(Array.Empty<OcrResultRegion>());
            }
            private static OcrResultRegion Region(string text, int x, int y, int width, int height) =>
                new(new RotatedRect(new Point2f(x + width / 2f, y + height / 2f), new Size2f(width, height), 0), text, 1f);
        }
    }

    private sealed class PreparedUidClock : TimeProvider
    {
        private readonly FakeTimeProvider _manual = new(DateTimeOffset.UtcNow);
        private readonly long _origin;
        private readonly long _anchor;
        internal PreparedUidClock() { _origin = _manual.GetTimestamp(); _anchor = TimeProvider.System.GetTimestamp(); }
        public override long TimestampFrequency => TimeProvider.System.TimestampFrequency;
        public override DateTimeOffset GetUtcNow() => _manual.GetUtcNow();
        public override long GetTimestamp() => _anchor + _manual.GetElapsedTime(_origin, _manual.GetTimestamp()).Ticks * TimestampFrequency / TimeSpan.TicksPerSecond;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            _manual.CreateTimer(callback, state, dueTime, period);
        internal void Advance(TimeSpan amount) => _manual.Advance(amount);
    }

    private sealed class FakeIo : ICurrentPlayerUidIo
    {
        internal FakeTimeProvider Clock { get; } = new();
        internal DateTimeOffset Started;
        private readonly Guid _session = Guid.NewGuid();
        private long _sequence;
        internal bool Menu, BlockReturn, BlockMenu, MeasuredLatency;
        private bool _openingFramePending;
        internal int ExtraOpeningFrames, ExtraNonconfirmingWorldFrames, ConfirmedWorldCaptures;
        internal int WorldCaptureMs = 600, ExtraMenuReadMs;
        internal bool ForeignWorld, ReplayWorld;
        internal bool HideOwnedEscapeReceipt, DropOwnedEscapeSubmit, HideFallbackActReceipt;
        internal List<double> PostInputCaptureGapsMs { get; } = new();
        private long? _inputReturnedAt;
        private UiSnapshot? _lastWorld;
        private readonly Guid _foreignSession = Guid.NewGuid();
        internal int MenuInputs, EscapeInputs, WorldCapturesAfterReturn, Captures, MenuOcrReads;
        internal string? Footer;
        internal string Defect = string.Empty;
        internal Action? AfterMenuRead;
        private bool _returned;
        internal FakeIo() { Started = Clock.GetUtcNow(); }
        public UiSnapshot Capture()
        {
            if (_inputReturnedAt is { } returned) { PostInputCaptureGapsMs.Add(Clock.GetElapsedTime(returned).TotalMilliseconds); _inputReturnedAt = null; }
            var source = new CaptureFrameStamp(_session, ++_sequence, Clock.GetTimestamp(), Clock.TimestampFrequency, Clock.GetUtcNow());
            Clock.Advance(TimeSpan.FromMilliseconds(MeasuredLatency ? (_returned && !Menu ? WorldCaptureMs : 600) : 20));
            if (_openingFramePending) { if (ExtraOpeningFrames > 0) ExtraOpeningFrames--; else { _openingFramePending = false; Menu = !BlockMenu; } return WorldOpeningFrame(source); }
            Captures++;
            if (_returned && !Menu) WorldCapturesAfterReturn++;
            var confirmingWorld = !Menu;
            if (_returned && !Menu && ExtraNonconfirmingWorldFrames > 0) { ExtraNonconfirmingWorldFrames--; confirmingWorld = false; }
            if (_returned && confirmingWorld) ConfirmedWorldCaptures++;
            var scene = new UiSnapshot(_sequence) { MainHud = confirmingWorld, MenuBack = Menu }.WithSource(source, Clock, UiSnapshot.RecoveryMaximumAge);
            if (_returned && !Menu)
            {
                if (ReplayWorld && _lastWorld != null) return _lastWorld;
                if (ForeignWorld) scene = scene.WithSource(new(_foreignSession, _sequence, source.CapturedTimestamp, source.TimestampFrequency, source.CapturedAt), Clock, UiSnapshot.RecoveryMaximumAge);
                _lastWorld = scene;
            }
            return scene;
        }
        private UiSnapshot WorldOpeningFrame(CaptureFrameStamp source)
        {
            Captures++;
            return new UiSnapshot(_sequence) { MainHud = true }.WithSource(source, Clock, UiSnapshot.RecoveryMaximumAge);
        }
        public CurrentPlayerUidFrame ReadUidFrame(Func<CurrentPlayerUidFrame, CurrentPlayerUidInput?>? chooseInput = null, bool readMenuUid = true)
        {
            if (Defect == "normal-end") throw new NormalEndException("synthetic normal end");
            if (Defect == "capture-failed") throw new InvalidOperationException("synthetic capture unavailable");
            var scene = Capture();
            var text = new List<CurrentPlayerUidText>();
            if (!Menu && Footer != null) text.Add(new(Footer, 1685, 1053, 220, 23));
            if (Menu && readMenuUid)
            {
                MenuOcrReads++;
                if (Defect != "missing-card") text.AddRange(new[] {
                    new CurrentPlayerUidText("冒险等阶", 315, 170, 100, 24),
                    new CurrentPlayerUidText("世界等级", 315, 250, 100, 24),
                    new CurrentPlayerUidText("生日", 315, 290, 50, 24),
                    new CurrentPlayerUidText("队伍配置", 300, 440, 100, 24) });
                if (Defect != "no-uid") text.Add(new(Defect switch {
                    "missing-label" => "123456789", "overflow" => "UID 9999999999",
                    "unrelated-digits" => "UID: 等级60 123456789", "split" => "UID:", _ => "UID 123456789"
                }, 115, 195, Defect == "split" ? 40 : 182, 30));
                if (Defect == "split") text.Add(new("123456789", 159, 195, 125, 30));
                if (Defect == "conflicting") text.Add(new("UID 234567891", 115, 200, 182, 30));
                if (Defect == "wrong-page") scene = scene with { MenuBack = false };
                if (Defect == "wrong-session") scene = scene.WithSource(scene.SourceStamp with { SessionId = Guid.NewGuid() }, Clock, UiSnapshot.RecoveryMaximumAge);
                if (MeasuredLatency) Clock.Advance(TimeSpan.FromMilliseconds(460 + ExtraMenuReadMs));
                AfterMenuRead?.Invoke();
            }
            var frame = new CurrentPlayerUidFrame(scene, 1920, 1080, text);
            var input = chooseInput?.Invoke(frame);
            if (input?.Action == UiAction.OpenMenu)
            {
                UiOperation.Current!.Token.ThrowIfCancellationRequested();
                MenuInputs++; Menu = !BlockMenu; if (MeasuredLatency) { Menu = false; _openingFramePending = true; }
                Clock.Advance(TimeSpan.FromMilliseconds(60));
                _inputReturnedAt = Clock.GetTimestamp();
                return frame with { SubmittedInput = UiAction.OpenMenu };
            }
            if (input?.Action == UiAction.Escape)
            {
                if (!DropOwnedEscapeSubmit) SubmitEscape(UiOperation.Current!.Token);
                _inputReturnedAt = Clock.GetTimestamp();
                return HideOwnedEscapeReceipt ? frame : frame with { SubmittedInput = UiAction.Escape };
            }
            return frame;
        }
        public Task<bool> OpenMenuAsync(UiSnapshot observed, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); MenuInputs++; Menu = !BlockMenu; if (MeasuredLatency) { Menu = false; _openingFramePending = true; Clock.Advance(TimeSpan.FromMilliseconds(420)); } Clock.Advance(TimeSpan.FromMilliseconds(60)); return Task.FromResult(true); }
        private bool SubmitEscape(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); EscapeInputs++; if (!BlockReturn) { Menu = false; _returned = true; } Clock.Advance(TimeSpan.FromMilliseconds(60)); return true; }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (MeasuredLatency) Clock.Advance(TimeSpan.FromMilliseconds(600)); var submitted = SubmitEscape(ct); _inputReturnedAt = Clock.GetTimestamp(); return Task.FromResult(HideFallbackActReceipt ? false : submitted); }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); return Task.CompletedTask; }
    }
}
