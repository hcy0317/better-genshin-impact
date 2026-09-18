using System.Diagnostics;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedMovementPermissionTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public void RecordedHudControlAndTargetMustJointlyAuthorizeMovementWithinTheDecisionBudget()
    {
        var directory = Environment.GetEnvironmentVariable("BGI_RECORDED_MOVEMENT_DIR");
        Assert.True(Directory.Exists(directory), "需要明确的已有截图目录，不采集桌面");
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        var source = new CaptureFrameSource();
        var battle = Guid.NewGuid();
        CombatBattleObservation Read(Mat pixels)
        {
            AutoFightSeek.ResetSeekState();
            using var frame = new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = source.Next() };
            var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame.Width, frame.Height), ocr, "复苏", "使用道具复苏角色");
            var hud = detector.IsCombatHud(frame);
            var control = CombatMotionReader.ReadControl(frame, hud, ocr);
            var target = AvatarRecognition.ReadPassiveTarget(frame);
            return new(frame.FrameStamp, battle, hud ? CombatObservationQuality.Available : CombatObservationQuality.Unavailable,
                target.Visual == null ? null : target, frame.Width, frame.Height) { Control = control, Motion = control.Motion };
        }

        string? positive = null;
        // 固定到本次已知日志日期，避免未来截图悄悄改变此证据批次。
        var paths = new[] { "error-202609170418055002.png", "error-202609161853532830.png" }
            .Select(file => Path.Combine(directory!, file));
        foreach (var path in paths)
        {
            using var pixels = Cv2.ImRead(path);
            var observation = Read(pixels);
            var allowed = CombatBattleHost.CanApproach(observation, battle, TimeProvider.System);
            output.WriteLine($"RECORDING {Path.GetFileName(path)} hud={observation.Quality} control={observation.Control.IsObserved}/{observation.Control.KeyboardBreakoutRequested} motion={observation.Motion} target={observation.Target} permit={allowed}");
            if (allowed) positive ??= path;
        }
        Assert.NotNull(positive); // 不用手工Normal或合成敌人位置代替生产可达性。
        using var sample = Cv2.ImRead(positive!);
        double Measure()
        {
            var started = Stopwatch.GetTimestamp();
            var observed = Read(sample);
            Assert.Equal(MotionStatus.Unknown, observed.Motion);
            Assert.True(CombatBattleHost.CanApproach(observed, battle, TimeProvider.System));
            return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var first = Measure();
        for (var i = 0; i < 10; i++) Measure();
        for (var batch = 1; batch <= 3; batch++)
        {
            var values = new List<double>();
            var consecutive = 0; var maximumConsecutive = 0;
            for (var i = 0; i < 200; i++)
            {
                var value = Measure(); values.Add(value);
                consecutive = value > 150 ? consecutive + 1 : 0;
                maximumConsecutive = Math.Max(maximumConsecutive, consecutive);
            }
            var ordered = values.Order().ToArray();
            var overruns = ordered.Count(value => value > 150);
            output.WriteLine("MOVEMENT_NATIVE_A " + JsonConvert.SerializeObject(new
            { Batch = batch, FirstMs = first, Count = values.Count, P95 = ordered[189], Max = ordered[^1], Overruns = overruns,
                MaximumConsecutive = maximumConsecutive, Recording = Path.GetFileName(positive),
                Plane = "recorded native HUD + control OCR + target recognition + permission; synthetic source stamps, no OS input or live capture", Samples = values }));
            Assert.True(ordered[189] <= 150 && overruns <= 2 && maximumConsecutive < 3);
        }
        foreach (var file in new[] { "s32-food-revive-20260916.png", "s32-map-marker-20260916.png", "s32-cannon-20260916.png" })
        {
            using var pixels = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", file));
            Assert.False(CombatBattleHost.CanApproach(Read(pixels), battle, TimeProvider.System));
        }
        using var frozen = Cv2.ImRead(Environment.GetEnvironmentVariable("BGI_RECORDED_FREEZE_FRAME")!);
        Assert.False(CombatBattleHost.CanApproach(Read(frozen), battle, TimeProvider.System));
        Assert.Null(System.Windows.Application.Current);
    }
}
