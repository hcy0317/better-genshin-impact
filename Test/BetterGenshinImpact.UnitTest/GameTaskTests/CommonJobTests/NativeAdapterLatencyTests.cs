using System.Diagnostics;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using OpenCvSharp;
using Xunit.Abstractions;
using BetterGenshinImpact.Helpers;
using Serilog;
using Serilog.Extensions.Logging;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class NativeAdapterLatencyTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public async Task ColdOcrWithARealSpacePromptCannotBlockThePureEStepOrPretendControlWasObserved()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var ocrFactory = new OcrFactory(NullLogger<BgiOnnxFactory>.Instance, () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test native initialization not released");
            return new PreparedControlOcr();
        });
        var preparing = ocrFactory.PrepareAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        using var source = Cv2.ImRead(Environment.GetEnvironmentVariable("BGI_RECORDED_FREEZE_FRAME")!);
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var burst = factory.CreateYoloPredictor(BgiOnnxModel.BgiQClassify);
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", source.Width, source.Height),
            ocrFactory.PaddleOcr, "复苏", "使用道具复苏角色");
        using var io = new RecordedIo(source, detector, ocrFactory.PaddleOcr, burst, actorName: "香菱");
        using var runner = NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("香菱 e(required)"), io);
        var step = Task.Run(async () => await runner.StepAsync(default));
        try
        {
            Assert.Same(step, await Task.WhenAny(step, Task.Delay(150)));
            Assert.Equal(CombatFlowResult.AwaitingObservation, (await step).Result);
            using var frame = new ImageRegion(source.Clone(), 0, 0);
            var control = CombatMotionReader.ReadControl(frame, true, ocrFactory.PaddleOcr);
            Assert.False(control.IsObserved);
            Assert.False(control.KeyboardBreakoutRequested);
            Assert.Equal(0, io.ControlPulses);
        }
        finally
        {
            release.Set();
            await preparing;
            await step;
        }
    }

    private sealed class PreparedControlOcr : IOcrService
    {
        public string Ocr(Mat mat) => "Space";
        public string OcrWithoutDetector(Mat mat) => "Space";
        public OcrResult OcrResult(Mat mat) => new([]);
    }

    [OfflineNativeDecisionFact]
    public async Task RecordedControlRecoveryIncludesTheRealFourStateGatewayAndLogProducer()
    {
        var path = Environment.GetEnvironmentVariable("BGI_RECORDED_FREEZE_FRAME");
        Assert.True(!string.IsNullOrEmpty(path) && File.Exists(path));
        using var source = Cv2.ImRead(path!);
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var burst = factory.CreateYoloPredictor(BgiOnnxModel.BgiQClassify);
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", source.Width, source.Height), ocr, "复苏", "使用道具复苏角色");
        using var io = new RecordedIo(source, detector, ocr, burst, controlInputs: true, actorName: "香菱");
        var program = CombatFlowProgram.Compile(CombatScriptParser.ParseContext("香菱 e(required)"));
        async Task<double> Measure()
        {
            var before = io.ControlPulses;
            var started = Stopwatch.GetTimestamp();
            using (var runner = NativeCombatFlowRunner.Create(program, io))
            {
                Assert.Equal(CombatFlowResult.AwaitingObservation, (await runner.StepAsync(default)).Result);
                Assert.Equal(CombatFlowResult.AwaitingObservation, (await runner.StepAsync(default)).Result);
                Assert.Equal(before + 1, io.ControlPulses);
            }
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var first = await Measure();
        for (var i = 0; i < 10; i++) await Measure();
        for (var batch = 1; batch <= 3; batch++)
        {
            var samples = new List<double>();
            var consecutive = 0; var maximumConsecutive = 0;
            for (var i = 0; i < 200; i++)
            {
                var value = await Measure(); samples.Add(value);
                consecutive = value > 150 ? consecutive + 1 : 0;
                maximumConsecutive = Math.Max(maximumConsecutive, consecutive);
            }
            var ordered = samples.Order().ToArray();
            var overruns = ordered.Count(value => value > 150);
            output.WriteLine("NATIVE_RECOVERY_PROBE " + JsonConvert.SerializeObject(new
            { Batch = batch, Count = samples.Count, First = first, P50 = ordered[99], P95 = ordered[189], P99 = ordered[197],
                Max = ordered[^1], Overruns = overruns, MaximumConsecutiveOverruns = maximumConsecutive,
                Plane = "recorded native observation + real recovery/kernel/input gateway + log producer + 50ms frame wait; OS focus/key calls substituted, C unavailable", Samples = samples }));
            Assert.True(ordered[189] <= 150 && overruns <= 2 && maximumConsecutive < 3);
        }
        Assert.Null(System.Windows.Application.Current);
    }

    [OfflineNativeDecisionFact]
    public async Task RecordedFrozenControlPreparationIncludesRealRecognitionAndLoggingWithoutAnyInput()
    {
        var path = Environment.GetEnvironmentVariable("BGI_RECORDED_FREEZE_FRAME");
        Assert.True(!string.IsNullOrEmpty(path) && File.Exists(path), "缺少已录制冻结画面，不可用合成图替代");
        using var source = Cv2.ImRead(path!);
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var burst = factory.CreateYoloPredictor(BgiOnnxModel.BgiQClassify);
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", source.Width, source.Height), ocr, "复苏", "使用道具复苏角色");
        using var io = new RecordedIo(source, detector, ocr, burst);
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var adapterOwner = (IDisposable)adapter;
        using var context = new CombatFlowContext();
        async Task<double> Measure()
        {
            var action = new CombatFlowAction(new CombatCommand("离线角色", "e"), context, () => true, context.Now + 5);
            var started = Stopwatch.GetTimestamp();
            adapter.BeginStep();
            Assert.Equal(CombatObservationPreparation.AwaitingObservation,
                await adapter.PrepareObservationStepAsync(action, "onfield", default));
            Assert.Null(action.InputAt);
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var first = await Measure();
        for (var i = 0; i < 10; i++) await Measure();
        for (var batch = 1; batch <= 3; batch++)
        {
            var samples = new List<double>();
            var consecutive = 0;
            var maximumConsecutive = 0;
            for (var i = 0; i < 200; i++)
            {
                var value = await Measure(); samples.Add(value);
                consecutive = value > 150 ? consecutive + 1 : 0;
                maximumConsecutive = Math.Max(maximumConsecutive, consecutive);
            }
            var ordered = samples.Order().ToArray();
            var overruns = ordered.Count(value => value > 150);
            output.WriteLine("NATIVE_CONTROL_PROBE " + JsonConvert.SerializeObject(new
            { Batch = batch, Count = samples.Count, First = first, P50 = ordered[99], P95 = ordered[189], P99 = ordered[197],
                Max = ordered[^1], Overruns = overruns, MaximumConsecutiveOverruns = maximumConsecutive,
                Logging = "real bounded producer + temporary file sink; no WPF/OS input", Samples = samples }));
            Assert.True(ordered[189] <= 150 && overruns <= 2 && maximumConsecutive < 3);
        }
        Assert.Null(System.Windows.Application.Current);
    }

    [OfflineNativeDecisionFact]
    public async Task RecordedFrameNativeAdapterMeasuresRecognitionConditionAndAdmissionTogether()
    {
        Assert.Null(System.Windows.Application.Current);
        var initialization = Stopwatch.StartNew();
        using var source = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "inactive-zhongli-20260911.png"));
        Assert.False(source.Empty());
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var burst = factory.CreateYoloPredictor(BgiOnnxModel.BgiQClassify);
        await burst.WarmUpAsync(NullLogger.Instance, default);
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", source.Width, source.Height), ocr, "复苏", "使用道具复苏角色");
        using var io = new RecordedIo(source, detector, ocr, burst);
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var adapterOwner = (IDisposable)adapter;
        using var context = new CombatFlowContext();
        var command = new CombatCommand("离线角色", "moveby(0,0)");
        var predicate = ConditionEvaluator.Compile("onfield(离线角色)");
        // 模型初始化/首次双帧选择单列，不把其物理等待计作一次战斗判定。
        var preparation = new CombatFlowAction(command, context, () => true, context.Now + 5);
        CombatObservationPreparation prepared;
        do
        {
            adapter.BeginStep();
            await adapter.AdvanceObservationAsync(default);
            prepared = await adapter.PrepareObservationStepAsync(preparation, "onfield", default);
            if (prepared == CombatObservationPreparation.AwaitingObservation) await Task.Delay(50);
        } while (prepared == CombatObservationPreparation.AwaitingObservation);
        Assert.Equal(CombatObservationPreparation.Ready, prepared);
        using (var sample = new ImageRegion(source.Clone(), 0, 0))
        using (var area = sample.DeriveCrop(AutoFightAssets.Get(sample).QRectForClassify))
            await burst.PrepareClassificationAsync(area.CacheImage, NullLogger.Instance, default);
        adapter.BeginStep();
        initialization.Stop();

        async Task<Sample> Measure()
        {
            io.CloneMilliseconds = 0;
            var total = Stopwatch.StartNew();
            adapter.BeginStep();
            Assert.Equal(true, adapter.Observe("onfield", [], "离线角色"));
            _ = adapter.Observe("e-ready", [], "离线角色");
            _ = adapter.Observe("q-ready", [], "离线角色");
            _ = adapter.Observe("q-ready", [], "离线后台");
            var motionStarted = total.Elapsed.TotalMilliseconds;
            Assert.Equal(MotionStatus.Unknown, io.ReadMotion()); // EQ标签不足以证明地面运动能力。
            var motionMs = total.Elapsed.TotalMilliseconds - motionStarted;
            var visionEnd = total.Elapsed.TotalMilliseconds;
            bool Ready() => predicate.EvaluateBoolean((function, args) => adapter.Observe(function, args, "离线角色")) == true;
            Assert.True(Ready());
            var conditionEnd = total.Elapsed.TotalMilliseconds;
            var action = new CombatFlowAction(command, context, Ready, context.Now + 1);
            Assert.Equal(CombatFlowResult.Succeeded, await adapter.ExecuteAsync(action, default));
            Assert.NotNull(action.InputAt);
            var ended = total.Elapsed.TotalMilliseconds;
            Assert.True(io.CloneMilliseconds > 0, "不能把只读缓存的成本当作原生判定总成本");
            return new(io.CloneMilliseconds, visionEnd - io.CloneMilliseconds,
                conditionEnd - visionEnd, ended - conditionEnd, ended,
                io.Clock.GetElapsedTime(io.LastSource.CapturedTimestamp).TotalMilliseconds, motionMs);
        }

        var first = await Measure();
        for (var i = 0; i < 10; i++) await Measure();
        using var process = Process.GetCurrentProcess();
        for (var batch = 1; batch <= 3; batch++)
        {
            process.Refresh();
            var privateBytesBefore = process.PrivateMemorySize64;
            var managedBytesBefore = GC.GetTotalMemory(false);
            var samples = new List<Sample>();
            var consecutive = 0;
            var maximumConsecutive = 0;
            for (var i = 0; i < 200; i++)
            {
                var sample = await Measure();
                samples.Add(sample);
                consecutive = sample.TotalMs > 150 ? consecutive + 1 : 0;
                maximumConsecutive = Math.Max(maximumConsecutive, consecutive);
            }
            var times = samples.Select(sample => sample.TotalMs).Order().ToArray();
            double P(double p) => times[(int)Math.Ceiling(p * times.Length) - 1];
            var overruns = times.Count(value => value > 150);
            process.Refresh();
            output.WriteLine("NATIVE_ADAPTER_PROBE " + JsonConvert.SerializeObject(new
            {
                Plane = "A: fixed recorded frame + real HUD/OCR/ONNX/motion + production NativeGame + condition + admission; no OS input",
                Batch = batch, Count = times.Length, P50 = P(.5), P95 = P(.95), P99 = P(.99), Max = times[^1],
                First = first, InitializationMs = initialization.Elapsed.TotalMilliseconds, Overruns = overruns,
                MaximumConsecutiveOverruns = maximumConsecutive, RealCaptureMs = (double?)null,
                PrivateBytesBefore = privateBytesBefore, PrivateBytesAfter = process.PrivateMemorySize64,
                ManagedBytesBefore = managedBytesBefore, ManagedBytesAfter = GC.GetTotalMemory(false),
                ResourceMeasurement = "native 200-sample loop without forced GC; diagnostic deltas, not a long-run leak proof",
                ActualGameSourceAgeMs = (double?)null, RecordedReplaySource = "synthetic replay stamps, not live game freshness proof",
                Logging = "real bounded producer + temporary file sink; no WPF/OS input",
                Samples = samples
            }));
            Assert.True(P(.95) <= 150 && overruns <= 2 && maximumConsecutive < 3);
        }
        Assert.Null(System.Windows.Application.Current);
    }

    private sealed record Sample(double RecordedCloneMs, double RecognitionMs, double ConditionMs, double AdmissionMs, double TotalMs, double ReplayAgeMs, double MotionMs);

    private sealed class RecordedIo : INativeCombatIo, IDisposable
    {
        private readonly Mat _source;
        private readonly ReviveUiDetector _detector;
        private readonly IOcrService _ocr;
        private readonly BgiYoloPredictor _burst;
        private readonly CaptureFrameSource _producer = new();
        private readonly DirectoryInfo _logDirectory;
        private readonly ApplicationLogPipeline _logs;
        private readonly SerilogLoggerProvider _loggerProvider;
        private ImageRegion? _latestFrame;
        public MotionStatus ReadMotion() => CombatMotionReader.Read(_latestFrame!, true, _ocr);
        public CombatControlObservation ReadControl(ImageRegion frame) => CombatMotionReader.ReadControl(frame, true, _ocr);
        public double CloneMilliseconds { get; set; }
        public CaptureFrameStamp LastSource { get; private set; }
        public TimeProvider Clock => TimeProvider.System;
        public ILogger Logger { get; }
        public ICombatHostInputDevice? ControlDevice { get; }
        public int ControlPulses { get; private set; }
        public CombatInputCoordinator InputCoordinator { get; } = new();
        public IReadOnlyList<NativeCombatActor> Actors { get; }
        public double LastFinishCheckAge => 0;
        internal RecordedIo(Mat source, ReviveUiDetector detector, IOcrService ocr, BgiYoloPredictor burst, bool controlInputs = false,
            string actorName = "离线角色")
        {
            _source = source; _detector = detector; _ocr = ocr; _burst = burst;
            _logDirectory = Directory.CreateTempSubdirectory("bgi-native-probe-logs-");
            _logs = new(new LoggerConfiguration().MinimumLevel.Debug()
                .WriteTo.File(Path.Combine(_logDirectory.FullName, "probe.log"), shared: true));
            _loggerProvider = new(_logs.Logger, dispose: false);
            Logger = _loggerProvider.CreateLogger("NativeDecisionProbe");
            if (controlInputs) ControlDevice = new RecordedControlDevice(this);
            using var frame = Capture()!;
            var active = ReadActive(frame, new());
            Assert.InRange(active, 1, 4);
            Actors = [new(actorName, active), new("离线后台", active % 4 + 1)];
        }
        public ImageRegion? Capture()
        {
            var start = Stopwatch.GetTimestamp();
            LastSource = _producer.Next();
            var frame = new ImageRegion(_source.Clone(), 0, 0) { FrameStamp = LastSource };
            _latestFrame = frame;
            CloneMilliseconds += Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            return frame;
        }
        public bool IsCombatHud(ImageRegion frame) => _detector.IsCombatHud(frame);
        public bool IsMainUi(ImageRegion frame) => IsCombatHud(frame);
        public int ReadActive(ImageRegion frame, AvatarActiveCheckContext context) => PartyAvatarSideIndexHelper.GetAvatarIndexIsActiveWithContext(
            frame, AutoFightAssets.Get(frame).AvatarIndexRectList.ToArray(), context);
        public bool? IsActorActive(NativeCombatActor actor, ImageRegion frame) => ReadActive(frame, new()) == actor.Index;
        public double ReadSkillCooldown(NativeCombatActor actor, ImageRegion frame) => CombatHudReader.ReadCooldown(frame, _ocr).Seconds;
        public bool IsSkillReady(NativeCombatActor actor, ImageRegion frame, double cooldown) =>
            IsActorActive(actor, frame) == true && cooldown <= 0 && !CombatHudReader.HasCooldownPixels(frame, false);
        public BurstObservation ReadBurst(ImageRegion frame, bool active) => CombatHudReader.ReadBurst(frame, _burst).Observation;
        public bool ReadLowHp(ImageRegion frame) => throw new NotSupportedException();
        public HashSet<int> ReadSideBurstReady(ImageRegion frame) => CombatHudReader.ReadSideBurstReady(frame);
        public bool TryGetKnownSkillCooldown(string actor, out double cooldown) { cooldown = 0; return false; }
        public void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc) => throw new NotSupportedException();
        public Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken ct) => throw new NotSupportedException();
        public CombatBattleHostInputResult SelectActor(int index, CombatNativeInputRequest request, CancellationToken ct) => throw new InvalidOperationException("录制前台应已匹配；不允许实际切人");
        public void WaitForSelection(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct).GetAwaiter().GetResult();
        public bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken ct) => false;
        public void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<ImageRegion> selection, CancellationToken ct) => throw new InvalidOperationException("录制HUD不能进入恢复");
        public void CheckDefeated(ImageRegion frame, CancellationToken ct) => throw new NotSupportedException();
        public CombatBattleHostInputResult SubmitInput(NativeCombatActor actor, CombatCommand command,
            CombatNativeInputRequest request, Action begin, CancellationToken ct)
        {
            Assert.Equal(Method.MoveBy, command.Method);
            begin();
            return new(CombatBattleHostInputStatus.Sent, Clock.GetTimestamp());
        }
        public void ReleaseInput() { }
        public Task DelayAsync(int milliseconds, CancellationToken ct) => Task.Delay(milliseconds, ct);
        public Task PrepareVisionAsync(CancellationToken ct) => Task.CompletedTask;
        public IDisposable BeginExclusive(bool allowPassiveObservation) => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
        private sealed class RecordedControlDevice(RecordedIo owner) : ICombatHostInputDevice
        {
            public TimeProvider Clock => owner.Clock;
            public ILogger Logger => owner.Logger;
            public void PrepareInput() { }
            public void PressBreakout() => owner.ControlPulses++;
            public void MoveCamera(int x, int y) => throw new InvalidOperationException("unexpected camera input");
            public void MoveForward(bool down) => throw new InvalidOperationException("unexpected movement");
            public void PressDrop() => throw new InvalidOperationException("unexpected drop");
            public void PressParty() => throw new InvalidOperationException("unexpected party input");
            public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => new(owner.DelayAsync(milliseconds, ct));
        }
        public void Dispose()
        {
            _loggerProvider.Dispose();
            _logs.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _logDirectory.Delete(true);
        } // 图像、OCR和模型由测试调用方拥有。
    }
}
