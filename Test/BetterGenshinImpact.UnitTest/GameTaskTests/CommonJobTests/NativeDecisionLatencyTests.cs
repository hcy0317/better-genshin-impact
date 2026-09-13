using System.Diagnostics;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[CollectionDefinition("OfflineNativeDecision", DisableParallelization = true)]
public class OfflineNativeDecisionCollection;

public sealed class OfflineNativeDecisionFactAttribute : FactAttribute
{
    public OfflineNativeDecisionFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BGI_OFFLINE_DECISION_PROBE") != "1")
            Skip = "Explicit offline native probe; model assets are required. Skipped is not latency verification.";
    }
}

/// <summary>固定帧上的真实视觉实现。没有真实采图/输入，不能用于宣称完整端到端达标。</summary>
[Collection("OfflineNativeDecision")]
public class NativeDecisionLatencyTests(ITestOutputHelper output)
{
    private const double DecisionTargetMs = 150;

    [OfflineNativeDecisionFact]
    public void NativeCombatHudJudgmentFitsTheDecisionBudget()
    {
        Assert.Null(System.Windows.Application.Current);
        Assert.False(TaskContext.Instance().IsInitialized);
        Assert.Equal(AppContext.BaseDirectory, Global.StartUpPath);
        var init = Stopwatch.StartNew();
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var burstPredictor = factory.CreateYoloPredictor(BgiOnnxModel.BgiQClassify);
        burstPredictor.WarmUpAsync(NullLogger.Instance, default).GetAwaiter().GetResult();
        init.Stop();
        var failed = new List<string>();
        var baseline = Environment.GetEnvironmentVariable("BGI_DECISION_PROBE_BASELINE") == "1";

        foreach (var scenario in new[]
        {
            (Name: "normal-hud-revive-stage", File: "inactive-zhongli-20260911.png", Reads: 1, Expected: ReviveUiState.None),
            (Name: "switch-repeated-revive-stage", File: "inactive-zhongli-20260911.png", Reads: 3, Expected: ReviveUiState.None),
            (Name: "combat-hud-actor-e-q-combined", File: "inactive-zhongli-20260911.png", Reads: 1, Expected: ReviveUiState.None)
        })
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", scenario.File);
            using var source = Cv2.ImRead(path);
            Assert.False(source.Empty());
            var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", source.Width, source.Height),
                ocr, "复苏", "使用道具复苏角色");

            (double Frame, double Vision, double Total) Measure()
            {
                var total = Stopwatch.StartNew();
                using var frame = new ImageRegion(source.Clone(), 0, 0);
                var frameMs = total.Elapsed.TotalMilliseconds;
                var start = total.Elapsed.TotalMilliseconds;
                var result = ReviveUiState.None;
                for (var read = 0; read < scenario.Reads; read++) result = detector.Read(frame);
                if (scenario.Name == "combat-hud-actor-e-q-combined")
                {
                    _ = PartyAvatarSideIndexHelper.GetAvatarIndexIsActiveWithContext(frame,
                        AutoFightAssets.Get(frame).AvatarIndexRectList.ToArray(), new AvatarActiveCheckContext());
                    _ = CombatHudReader.ReadCooldown(frame, ocr);
                    _ = CombatHudReader.HasCooldownPixels(frame, burst: false);
                    _ = CombatHudReader.ReadBurst(frame, burstPredictor);
                }
                var completedAt = total.Elapsed.TotalMilliseconds;
                Assert.Equal(scenario.Expected, result); // 快但错误的识别不算性能通过。
                return (frameMs, completedAt - start, completedAt);
            }

            var first = Measure();
            for (var warmup = 0; warmup < 10; warmup++) Measure();
            for (var batch = 0; batch < 3; batch++)
            {
                var samples = new List<(double Frame, double Vision, double Total)>();
                var consecutiveOverruns = 0;
                for (var index = 0; index < 200; index++)
                {
                    var sample = Measure();
                    samples.Add(sample);
                    consecutiveOverruns = sample.Total > DecisionTargetMs ? consecutiveOverruns + 1 : 0;
                    // 基线确证连续超限即可失败停止；优化后的正式验收不能用短采样替代完整批次。
                    if (baseline && consecutiveOverruns >= 3) break;
                }
                var ordered = samples.Select(sample => sample.Total).Order().ToArray();
                double Percentile(double p) => ordered[(int)Math.Ceiling(p * ordered.Length) - 1];
                var overrunCount = ordered.Count(value => value > DecisionTargetMs);
                var exceeded = Percentile(.95) > DecisionTargetMs || overrunCount > samples.Count * .01 || consecutiveOverruns >= 3;
                output.WriteLine("NATIVE_DECISION_PROBE " + JsonConvert.SerializeObject(new
                {
                    EvidencePlane = "A: fixed-frame native components; not complete capture/selection/scheduling/admission pipeline",
                    scenario.Name, scenario.Reads, Batch = batch + 1, Width = source.Width, Height = source.Height,
                    Model = "PP-OCRv6 and q_classify_sim", Provider = string.Join(",", factory.ProviderTypes), CpuProcessors = Environment.ProcessorCount,
                    IntraOpThreads = 0, InterOpThreads = 0, Threads = "ORT production defaults",
                    InitializationMs = init.Elapsed.TotalMilliseconds, FirstJudgmentMs = first.Total,
                    CompleteBatch = samples.Count == 200, Count = samples.Count, TargetMs = DecisionTargetMs,
                    P50 = Percentile(.50), P95 = Percentile(.95), P99 = Percentile(.99), Max = ordered[^1],
                    OverrunCount = overrunCount, Exceeded = exceeded,
                    RealCaptureMs = (double?)null, SourceFrameAgeMs = (double?)null,
                    CaptureEndToEnd = "unverified", NoStart = true,
                    Samples = samples.Select(sample => new { RecordedFrameCloneMs = sample.Frame, VisionMs = sample.Vision, TotalMs = sample.Total })
                }));
                if (exceeded) failed.Add(scenario.Name + "/batch-" + (batch + 1));
                if (baseline && exceeded) break;
            }
        }
        Assert.Null(System.Windows.Application.Current);
        Assert.False(TaskContext.Instance().IsInitialized);
        Assert.True(failed.Count == 0, "Decision paths need redesign: " + string.Join(", ", failed));
    }
}
