using BetterGenshinImpact.GameTask.AutoDomain.Model;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.Area.Converter;
using BetterGenshinImpact.UnitTest.CoreTests.RecognitionTests.OCRTests;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ClearScript.V8;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoDomainTests
{
    [Collection("Init Collection")]
    public class ResinStatusTests
    {
        private readonly PaddleFixture paddle;

        public ResinStatusTests(PaddleFixture paddle)
        {
            this.paddle = paddle;
        }

        /// <summary>
        /// 测试识别四种树脂数量，数量应正确
        /// </summary>
        [Theory]
        [InlineData(@"AutoDomain\SelectRevitalization.png", 21, 0, 2, 1)]
        [InlineData(@"AutoDomain\SelectRevitalizationOcrV4.png", 11, 0, 1, 149, "V4")]
        [InlineData(@"AutoDomain\SelectRevitalizationOcrV4_NSSZ1.png", 20, 1, 1, 0, "V4")]
        [InlineData(@"AutoDomain\SelectRevitalizationOcrV4_NSSZ1.png", 20, 1, 1, 0)]
        public void RecogniseFromRegion_ResinStatusShouldBeRight(string screenshot1080p, int originalResinCount, int fragileResinCount, int condensedResinCount, int transientResinCount, string ocrVersion = "V5")
        {
            //
            Mat mat = new Mat(@$"..\..\..\Assets\{screenshot1080p}");
            var imageRegion = new ImageRegion(mat, 0, 0, converter: new ScaleConverter(1d));
            FakeSystemInfo systemInfo = new FakeSystemInfo(new Vanara.PInvoke.RECT(0, 0, mat.Width, mat.Height), 1);

            //
            var result = ResinStatus.RecogniseFromRegion(imageRegion, systemInfo, this.paddle.Get(version: ocrVersion));

            //
            Assert.Equal(originalResinCount, result.OriginalResinCount);
            Assert.Equal(condensedResinCount, result.CondensedResinCount);
        }

        [Theory]
        [InlineData("160/200", 160)]
        [InlineData("20/200", 20)]
        public void OriginalResinParser_RequiresARecognizedCount(string text, int expected)
        {
            Assert.Equal(expected, ResinStatus.ParseOriginalResinCount(text));
            Assert.Throws<InvalidOperationException>(() =>
                ResinStatus.ParseOriginalResinCount("树脂"));
        }

        [Theory]
        [InlineData("0", 0)]
        [InlineData("5", 5)]
        public void CondensedResinParser_DistinguishesZeroFromOcrFailure(string text, int expected)
        {
            Assert.Equal(expected, ResinStatus.ParseCondensedResinCount(text));
            Assert.Throws<InvalidOperationException>(() =>
                ResinStatus.ParseCondensedResinCount(string.Empty));
        }

        [Fact]
        public void InventoryResinCounts_AreStoredWithoutThrowing()
        {
            var status = new ResinStatus
            {
                FragileResinCount = 28,
                TransientResinCount = 2
            };

            Assert.Equal(28, status.FragileResinCount);
            Assert.Equal(2, status.TransientResinCount);
        }
    }

    public class MapResinRatioPureTests
    {
        [Theory]
        [InlineData("160／200", 160)]
        [InlineData("１６０／２００", 160)]
        [InlineData("160 / 200", 160)]
        [InlineData("2000/200", 2000)]
        public void OriginalRatioAcceptsNormalizedTextWithoutClampingCurrent(string text, int expected)
        {
            BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
            Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
            Assert.Null(System.Windows.Application.Current);
            Assert.Equal(expected, ResinStatus.ParseOriginalResinCount(text));
            Assert.Null(System.Windows.Application.Current);
        }

        [Theory]
        [InlineData("")]
        [InlineData("树脂")]
        public void UnrecognizedOriginalRatioDoesNotBecomeZero(string text)
        {
            BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
            Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
            Assert.Null(System.Windows.Application.Current);
            Assert.Throws<InvalidOperationException>(() => ResinStatus.ParseOriginalResinCount(text));
            Assert.Null(System.Windows.Application.Current);
        }

        [Theory]
        [InlineData("160|/|200", 160)]
        [InlineData("１６０|／|２００", 160)]
        [InlineData("2000 / 200", 2000)]
        [InlineData("160/200|160/200", 160)]
        [InlineData("211200", 21)]
        public void RatioTokensAreCombinedOnlyWhenTheyHaveOneUnambiguousValue(string text, int expected)
        {
            Prohibit();
            var value = ResinStatus.ParseTexts(text.Split('|'), true);
            Assert.True(value.Known);
            Assert.Equal(expected, value.Count);
        }

        [Theory]
        [InlineData("160/200|40/200")]
        [InlineData("160/200|160/300")]
        [InlineData("1|60|/|200")]
        [InlineData("160/200|garbage")]
        [InlineData("1/0")]
        [InlineData("160/999")]
        [InlineData("2147483648/200")]
        public void ConflictingOrMalformedRatioTokensRemainUnknown(string text)
        {
            Prohibit();
            var value = ResinStatus.ParseTexts(text.Split('|'), true);
            Assert.False(value.Known);
            Assert.Null(value.Count);
            Assert.Equal(text.Split('|'), value.RawText);
        }

        [Theory]
        [InlineData("５", 5)]
        [InlineData("x|5", 5)]
        [InlineData("0", 0)]
        public void WholeCondensedQuantityIsNotAZeroFallback(string text, int expected)
        {
            Prohibit();
            var value = ResinStatus.ParseTexts(text.Split('|'), false);
            Assert.True(value.Known);
            Assert.Equal(expected, value.Count);
            Assert.False(ResinStatus.ParseTexts(Array.Empty<string>(), false).Known);
            Assert.Null(ResinStatus.ParseTexts(new[] { "5", "4" }, false).Count);
        }

        [Fact]
        public void BothQuantitiesUseOneCallerFrameAndOwnedCropsWithTheOriginalSource()
        {
            Prohibit();
            var clock = new FakeTimeProvider();
            var source = new CaptureFrameSource(clock);
            var captures = 0;
            ImageRegion Capture() { captures++; return new(new Mat(80, 160, MatType.CV_8UC3), 0, 0) { FrameStamp = source.Next() }; }
            using var frame = Capture();
            var original = new Rect(10, 12, 60, 20);
            var condensed = new Rect(90, 12, 20, 20);
            var crops = new List<ImageRegion>();
            var reading = ResinStatus.ReadFromImage(frame, original, condensed, (crop, kind) =>
            {
                Assert.Same(frame, crop.Prev);
                Assert.Equal(frame.FrameStamp, crop.FrameStamp);
                var expected = kind == "original" ? original : condensed;
                Assert.Equal(expected, new Rect(crop.X, crop.Y, crop.Width, crop.Height));
                crops.Add(crop);
                return new(crop.FrameStamp, kind == "original" ? new[] { "160", "/", "200" } : new[] { "5" });
            }, clock, requireKnownSource: true);
            Assert.Equal(1, captures);
            Assert.Equal(2, crops.Count);
            Assert.All(crops, crop => Assert.True(crop.SrcMat.IsDisposed));
            Assert.False(frame.SrcMat.IsDisposed);
            Assert.Equal(frame.FrameStamp, reading.SourceBefore);
            Assert.Equal(reading.SourceBefore, reading.SourceAfter);
            Assert.Equal(160, reading.Original.Count);
            Assert.Equal(5, reading.Condensed.Count);
            Assert.Equal(original, reading.Original.Roi);
            Assert.Equal(new[] { "160", "/", "200" }, reading.Original.RawText);
        }

        [Fact]
        public void SlowOcrKeepsHistoricalQuantityDataWithoutGrantingFreshInputPermission()
        {
            Prohibit();
            var clock = new FakeTimeProvider();
            var source = new CaptureFrameSource(clock);
            using var frame = new ImageRegion(new Mat(20, 40, MatType.CV_8UC3), 0, 0) { FrameStamp = source.Next() };
            var reading = ResinStatus.ReadFromImage(frame, new Rect(0, 0, 20, 10), null, (crop, _) =>
            {
                clock.Advance(TimeSpan.FromSeconds(5));
                return new(crop.FrameStamp, new[] { "160 / 200" });
            }, clock, requireKnownSource: true);
            Assert.Equal(160, reading.Original.Count);
            Assert.Null(reading.Condensed.Count);
            Assert.False(frame.FrameStamp.IsFresh(clock, TimeSpan.FromSeconds(2)));
            Assert.True(reading.ContextUnchanged);
            Assert.True(reading.ReadCompleted > reading.ReadStarted);
            var scriptValue = (IDictionary<string, object?>)Dispatcher.ToScriptMapResin(reading, true);
            Assert.Equal(false, scriptValue["inputAuthorized"]);
        }

        [Theory]
        [InlineData("missing-icon")]
        [InlineData("empty")]
        [InlineData("ocr-exception")]
        [InlineData("foreign-ocr-source")]
        [InlineData("frame-source-changed")]
        [InlineData("runtime-changed")]
        [InlineData("unknown-source")]
        [InlineData("clock-mismatch")]
        public void ReaderFailuresNeverProduceAZeroQuantity(string fault)
        {
            Prohibit();
            var clock = new FakeTimeProvider();
            var source = new CaptureFrameSource(clock);
            var foreign = new CaptureFrameSource(clock);
            using var frame = new ImageRegion(new Mat(20, 40, MatType.CV_8UC3), 0, 0)
            { FrameStamp = fault == "unknown-source" ? default : source.Next() };
            if (fault == "clock-mismatch") frame.FrameStamp = frame.FrameStamp with { TimestampFrequency = clock.TimestampFrequency + 1 };
            var sameContext = true;
            var reading = ResinStatus.ReadFromImage(frame, fault == "missing-icon" ? null : new Rect(0, 0, 20, 10), null, (crop, _) =>
            {
                if (fault == "ocr-exception") throw new InvalidOperationException("OCR boundary failed");
                if (fault == "runtime-changed") sameContext = false;
                if (fault == "frame-source-changed") frame.FrameStamp = foreign.Next();
                return new(fault == "foreign-ocr-source" ? foreign.Next() : crop.FrameStamp,
                    fault == "empty" ? Array.Empty<string>() : new[] { "160/200" });
            }, clock, sameContext: () => sameContext, requireKnownSource: true);
            Assert.False(reading.Original.Known);
            Assert.Null(reading.Original.Count);
            Assert.Null(reading.Condensed.Count);
        }

        [Fact]
        public void NativeUnboundOfflineImagesKeepParsingSemanticsWithoutInventingAStamp()
        {
            Prohibit();
            using var frame = new ImageRegion(new Mat(20, 40, MatType.CV_8UC3), 0, 0);
            var result = ResinStatus.ReadFromImage(frame, new Rect(0, 0, 20, 10), null,
                (crop, _) => new(crop.FrameStamp, new[] { "160/200" }));
            Assert.Equal(160, result.Original.Count);
            Assert.False(result.SourceBefore.IsKnown);
            Assert.False(result.SourceAfter.IsKnown);
        }

        [Fact]
        public void CancellationAndParentDeadlineAreNotConvertedIntoUnknownOcr()
        {
            Prohibit();
            var clock = new FakeTimeProvider();
            var source = new CaptureFrameSource(clock);
            using var frame = new ImageRegion(new Mat(20, 40, MatType.CV_8UC3), 0, 0) { FrameStamp = source.Next() };
            using var cancelled = new CancellationTokenSource();
            Assert.ThrowsAny<OperationCanceledException>(() => ResinStatus.ReadFromImage(frame, new Rect(0, 0, 20, 10), null,
                (crop, _) => { cancelled.Cancel(); return new(crop.FrameStamp, new[] { "160/200" }); }, clock,
                validate: cancelled.Token.ThrowIfCancellationRequested));
            var deadline = new TimeoutException("parent deadline");
            Assert.Same(deadline, Assert.Throws<TimeoutException>(() => ResinStatus.ReadFromImage(frame, new Rect(0, 0, 20, 10), null,
                (_, _) => throw deadline, clock)));
        }

        [Fact]
        public void ExistingV8HostMarshalsTheOwnedCropCallbackAndHistoricalDataResult()
        {
            Prohibit();
            var clock = new FakeTimeProvider();
            var source = new CaptureFrameSource(clock);
            using var frame = new ImageRegion(new Mat(40, 100, MatType.CV_8UC3), 0, 0) { FrameStamp = source.Next() };
            var callbacks = 0;
            var crops = new List<ImageRegion>();
            var dependencies = new Dispatcher.MapResinReadDependencies(clock, image => ReferenceEquals(image, frame), () => true);
            var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance, mapResinRead: dependencies);
            using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
            engine.AddHostObject("dispatcher", dispatcher);
            engine.AddHostObject("frame", frame);
            Assert.True((bool)engine.Evaluate("typeof dispatcher.readMapResin === 'function'"));
            engine.AddHostObject("trackCrop", (Action<ImageRegion, string>)((crop, kind) =>
            {
                callbacks++;
                Assert.Same(frame, crop.Prev);
                Assert.Equal(frame.FrameStamp, crop.FrameStamp);
                Assert.Equal(kind == "original" ? new Rect(0, 0, 60, 20) : new Rect(70, 0, 20, 20),
                    new Rect(crop.X, crop.Y, crop.Width, crop.Height));
                crops.Add(crop);
                clock.Advance(TimeSpan.FromSeconds(3));
            }));
            engine.Execute(@"
                var reading = dispatcher.readMapResin(frame,
                    { original: { x:0,y:0,width:60,height:20 }, condensed: { x:70,y:0,width:20,height:20 } },
                    function(crop, kind) {
                        trackCrop(crop, kind);
                        return { source: crop.frameStamp, texts: kind === 'original' ? ['２０００', '／', '２００'] : ['0'] };
                    });
            ");
            Assert.Equal(2, callbacks);
            Assert.All(crops, crop => Assert.True(crop.SrcMat.IsDisposed));
            Assert.False(frame.SrcMat.IsDisposed);
            Assert.True((bool)engine.Evaluate("reading.schema === 'bgi.map-resin.v1' && reading.original.known && reading.original.count === 2000"));
            Assert.True((bool)engine.Evaluate("reading.condensed.known && reading.condensed.count === 0 && reading.inputAuthorized === false"));
            Assert.True((bool)engine.Evaluate("reading.contextUnchanged && reading.mapObserved && reading.sourceBefore.isKnown && reading.sourceBefore.sessionId === reading.sourceAfter.sessionId"));
            Assert.True((bool)engine.Evaluate("reading.original.rawText[0] === '２０００' && reading.original.roi.width === 60"));
            Assert.False(frame.FrameStamp.IsFresh(clock, TimeSpan.FromSeconds(2)));
            Assert.Null(System.Windows.Application.Current);
        }

        [Theory]
        [InlineData("missing-icons")]
        [InlineData("not-map")]
        [InlineData("serialized-stamp")]
        [InlineData("empty-text")]
        public void V8BridgeCannotInventZeroOrSourceAuthority(string fault)
        {
            Prohibit();
            var clock = new FakeTimeProvider();
            var source = new CaptureFrameSource(clock);
            using var frame = new ImageRegion(new Mat(20, 40, MatType.CV_8UC3), 0, 0) { FrameStamp = source.Next() };
            var dependencies = new Dispatcher.MapResinReadDependencies(clock, _ => fault != "not-map", () => true);
            var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance, mapResinRead: dependencies);
            using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
            engine.AddHostObject("dispatcher", dispatcher);
            engine.AddHostObject("frame", frame);
            engine.Execute("var fault = " + Newtonsoft.Json.JsonConvert.SerializeObject(fault) + ";");
            engine.Execute(@"
                var calls = 0;
                var reading = dispatcher.readMapResin(frame,
                    { original: fault === 'missing-icons' ? null : { x:0,y:0,width:20,height:10 }, condensed: null },
                    function(crop) {
                        calls++;
                        return { source: fault === 'serialized-stamp' ? { sessionId:'fake',sequence:1 } : crop.frameStamp,
                            texts: fault === 'empty-text' ? [] : ['160/200'] };
                    });
            ");
            Assert.True((bool)engine.Evaluate("!reading.original.known && reading.original.count === null && !reading.condensed.known && reading.condensed.count === null"));
            Assert.True((bool)engine.Evaluate("reading.inputAuthorized === false"));
            if (fault is "missing-icons" or "not-map") Assert.Equal(0, Convert.ToInt32(engine.Evaluate("calls")));
        }

        [Fact]
        public void ClosedRealTaskGuardRemainsTerminalDuringAnOcrCallback()
        {
            Prohibit();
            using var scope = TaskExecutionScope.BeginOwned();
            var guard = TaskExecutionScope.Capture();
            using var frame = new ImageRegion(new Mat(20, 40, MatType.CV_8UC3), 0, 0);
            Assert.ThrowsAny<OperationCanceledException>(() => ResinStatus.ReadFromImage(frame, new Rect(0, 0, 20, 10), null,
                (crop, _) => { scope.Dispose(); return new(crop.FrameStamp, new[] { "160/200" }); }, validate: guard.Check));
        }

        [Theory]
        [InlineData(160, -1, true)]
        [InlineData(20, -1, true)]
        [InlineData(-1, 2, true)]
        [InlineData(10, 0, false)]
        [InlineData(0, 0, false)]
        public void PartialNativeReaderDataUsesTheActualPreflightDecision(int original, int condensed, bool expected)
        {
            Prohibit();
            var observed = ReadNativeAmounts(original, condensed);
            var status = ResinStatus.ToNativeStatus(observed, allowPartial: true);
            Assert.Equal(original, status.OriginalResinCount);
            Assert.Equal(condensed, status.CondensedResinCount);
            Assert.Equal(expected, AutoDomainTask.HasConfirmedMapResinForPreflight(status));
        }

        [Theory]
        [InlineData(10, -1)]
        [InlineData(-1, 0)]
        public void PartialNonpositivePreflightRemainsUnconfirmedInsteadOfInsufficient(int original, int condensed)
        {
            Prohibit();
            var status = ResinStatus.ToNativeStatus(ReadNativeAmounts(original, condensed), allowPartial: true);
            Assert.Equal(original, status.OriginalResinCount);
            Assert.Equal(condensed, status.CondensedResinCount);
            Assert.Throws<InvalidOperationException>(() => AutoDomainTask.HasConfirmedMapResinForPreflight(status));
        }

        [Theory]
        [InlineData(160, -1)]
        [InlineData(-1, 2)]
        public void ExistingNativeReaderProjectionDefaultsToStrictForOtherCallers(int original, int condensed)
        {
            Prohibit();
            var observed = ReadNativeAmounts(original, condensed);
            Assert.Throws<InvalidOperationException>(() => ResinStatus.ToNativeStatus(observed));
            var partial = ResinStatus.ToNativeStatus(observed, allowPartial: true);
            Assert.Equal(original, partial.OriginalResinCount);
            Assert.Equal(condensed, partial.CondensedResinCount);
        }

        [Fact]
        public void AllUnknownNativeReadingNeverBecomesAnInsufficientOrZeroResult()
        {
            Prohibit();
            var observed = ReadNativeAmounts(-1, -1);
            Assert.Null(observed.Original.Count);
            Assert.Null(observed.Condensed.Count);
            Assert.Throws<InvalidOperationException>(() => ResinStatus.ToNativeStatus(observed, allowPartial: true));
            Assert.Throws<InvalidOperationException>(() => AutoDomainTask.HasConfirmedMapResinForPreflight(
                new ResinStatus { OriginalResinCount = -1, CondensedResinCount = -1 }));
        }

        private static ResinStatus.FrameReading ReadNativeAmounts(int original, int condensed)
        {
            var clock = new FakeTimeProvider();
            var source = new CaptureFrameSource(clock);
            using var frame = new ImageRegion(new Mat(20, 60, MatType.CV_8UC3), 0, 0) { FrameStamp = source.Next() };
            return ResinStatus.ReadFromImage(frame,
                original >= 0 ? new Rect(0, 0, 20, 10) : null,
                condensed >= 0 ? new Rect(30, 0, 20, 10) : null,
                (crop, kind) => new(crop.FrameStamp, new[] { kind == "original"
                    ? original.ToString(System.Globalization.CultureInfo.InvariantCulture) + "/200"
                    : condensed.ToString(System.Globalization.CultureInfo.InvariantCulture) }), clock);
        }

        [Fact]
        public async Task RealV8CallbackCodeFailureEscapesTheReaderToTheScriptOutcomeBoundary()
        {
            Prohibit();
            using var execution = TaskExecutionScope.BeginOwned();
            using var lifetime = new ScriptAsyncLifetime(default, NullLogger.Instance);
            var clock = new FakeTimeProvider();
            var source = new CaptureFrameSource(clock);
            using var frame = new ImageRegion(new Mat(20, 60, MatType.CV_8UC3), 0, 0) { FrameStamp = source.Next() };
            var dependencies = new Dispatcher.MapResinReadDependencies(clock, image => ReferenceEquals(image, frame), () => true, lifetime.Token);
            var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance, mapResinRead: dependencies);
            using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding | V8ScriptEngineFlags.EnableTaskPromiseConversion);
            engine.AddHostObject("dispatcher", dispatcher);
            engine.AddHostObject("frame", frame);
            engine.AddHostObject("assertLiveScript", (Action)(() =>
            {
                Assert.Same(lifetime, ScriptAsyncLifetime.Current);
                Assert.False(lifetime.Token.IsCancellationRequested);
                TaskExecutionScope.ThrowIfFailed();
            }));
            engine.Execute("globalThis.resinContinued = false; globalThis.resinCallbackCalls = 0;");
            ScriptExecutionResult? result = null;
            var error = await Record.ExceptionAsync(async () => result = await ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate(@"
                dispatcher.readMapResin(frame,
                    { original: {x:0,y:0,width:20,height:10}, condensed: {x:30,y:0,width:20,height:10} },
                    function mapResinOcrCallbackBug(crop, kind) {
                        assertLiveScript();
                        resinCallbackCalls++;
                        throw new Error('MAP_RESIN_CALLBACK_BUG');
                    });
                resinContinued = true;
                taskResult.report('Completed', 'MUST_NOT_REACH');
            "), default, outcomeContract: "explicit-v1", lifetime: lifetime));
            Assert.NotNull(error);
            Assert.Contains("MAP_RESIN_CALLBACK_BUG", error.ToString());
            Assert.Null(result);
            Assert.False((bool)engine.Evaluate("resinContinued"));
            Assert.Equal(1, Convert.ToInt32(engine.Evaluate("resinCallbackCalls")));
            Assert.Null(TaskExecutionScope.Failure); // A normal JS error is not the global combat/recovery latch.
            Assert.False(frame.SrcMat.IsDisposed);
            Assert.Null(System.Windows.Application.Current);
        }

        [Fact]
        public async Task RealV8EmptyOcrRemainsUnknownWithinTheSameLifetimeOutcomeContract()
        {
            Prohibit();
            using var execution = TaskExecutionScope.BeginOwned();
            using var lifetime = new ScriptAsyncLifetime(default, NullLogger.Instance);
            var clock = new FakeTimeProvider();
            var source = new CaptureFrameSource(clock);
            using var frame = new ImageRegion(new Mat(20, 60, MatType.CV_8UC3), 0, 0) { FrameStamp = source.Next() };
            var dependencies = new Dispatcher.MapResinReadDependencies(clock, image => ReferenceEquals(image, frame), () => true, lifetime.Token);
            var dispatcher = new Dispatcher(new object(), null!, NullLogger<Dispatcher>.Instance, mapResinRead: dependencies);
            using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding | V8ScriptEngineFlags.EnableTaskPromiseConversion);
            engine.AddHostObject("dispatcher", dispatcher);
            engine.AddHostObject("frame", frame);
            engine.AddHostObject("assertLiveScript", (Action)(() =>
            {
                Assert.Same(lifetime, ScriptAsyncLifetime.Current);
                Assert.False(lifetime.Token.IsCancellationRequested);
                TaskExecutionScope.ThrowIfFailed();
            }));
            var result = await ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate(@"
                globalThis.emptyOcrCalls = 0;
                var unknown = dispatcher.readMapResin(frame,
                    { original: {x:0,y:0,width:20,height:10}, condensed: null },
                    function(crop) {
                        assertLiveScript();
                        emptyOcrCalls++;
                        return { source: crop.frameStamp, texts: [] };
                    });
                globalThis.emptyWasUnknown = !unknown.original.known && unknown.original.count === null
                    && !unknown.condensed.known && unknown.condensed.count === null;
                taskResult.report('NeedsReconcile', 'RESIN_OCR_EMPTY');
            "), default, outcomeContract: "explicit-v1", lifetime: lifetime);
            Assert.Equal(ScriptOutcomeKind.NeedsReconcile, result.Kind);
            Assert.Equal("RESIN_OCR_EMPTY", result.Reason);
            Assert.True((bool)engine.Evaluate("emptyWasUnknown"));
            Assert.Equal(1, Convert.ToInt32(engine.Evaluate("emptyOcrCalls")));
            Assert.Null(TaskExecutionScope.Failure);
            Assert.False(frame.SrcMat.IsDisposed);
            Assert.Null(System.Windows.Application.Current);
        }

        private static void Prohibit()
        {
            BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
            Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
            Assert.Null(System.Windows.Application.Current);
        }
    }
}
