using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using System.Diagnostics;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class CannonUiReaderTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public async Task BurstPreparationExecutesTheRealClassifierOnceWithoutReusingItsResultAsLiveEvidence()
    {
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var predictor = factory.CreateYoloPredictor(BgiOnnxModel.BgiQClassify);
        using var frame = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "s32-mining-hud-20260916.png")), 0, 0);
        using var area = frame.DeriveCrop(AutoFightAssets.Get(frame).QRectForClassify);
        var preparation = Stopwatch.StartNew();
        Assert.True(await predictor.PrepareClassificationAsync(area.CacheImage, NullLogger.Instance, default));
        output.WriteLine($"ACTUAL_CLASSIFICATION_PREPARATION_MS={preparation.Elapsed.TotalMilliseconds:F2}");
        Assert.False(await predictor.PrepareClassificationAsync(area.CacheImage, NullLogger.Instance, default));
        using var fresh = new ImageRegion(frame.SrcMat.Clone(), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
        var decision = Stopwatch.StartNew();
        var result = CombatHudReader.ReadBurst(fresh, predictor);
        output.WriteLine($"FIRST_LIVE_CLASSIFICATION_MS={decision.Elapsed.TotalMilliseconds:F2} label={result.Label}");
        Assert.False(string.IsNullOrWhiteSpace(result.Label));
        Assert.True(decision.Elapsed.TotalMilliseconds <= 150);
    }

    [OfflineNativeDecisionFact]
    public async Task RecordedMarkerEditorReportsSelectionMismatchWithoutSendingTeleportOrSavingAMarker()
    {
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var frame = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Ui", "s32-map-marker-20260916.png")), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
        var inputs = 0;
        var failure = await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            TeleportPanelConfirmation.TryConfirmAsync(frame, _ => { inputs++; return Task.CompletedTask; }, default, ocr));
        Assert.Contains("标记编辑", failure.Message);
        Assert.DoesNotContain("未激活", failure.Message);
        Assert.Equal(0, inputs);
    }

    [OfflineNativeDecisionFact]
    public void RecordedCannonControlsAreRecognizedByProductionVisionWithoutGrantingOtherScenes()
    {
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        var frames = new CaptureFrameSource();
        foreach (var file in new[] { "s32-cannon-20260916.png", "s32-mining-hud-20260916.png",
                     "s32-food-revive-20260916.png", "food-revive-controller-20260910.png", "inactive-zhongli-20260911.png" })
        {
            using var frame = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", file)), 0, 0)
            { FrameStamp = frames.Next() };
            Assert.False(frame.SrcMat.Empty());
            var observed = CannonUiReader.Read(frame, ocr);
            output.WriteLine($"{file}: {observed}");
            var cannon = file == "s32-cannon-20260916.png";
            Assert.Equal(cannon, observed.CanFire);
            Assert.Equal(cannon, observed.CanExit);
            if (cannon)
            {
                Assert.True(observed.IsFor(frame.FrameStamp));
                var revive = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr,
                    "复苏", "使用道具复苏角色");
                var snapshot = NativeUiDriver.Read(frame, ocr: ocr, reviveDetector: revive);
                Assert.True(snapshot.Cannon);
                Assert.True(snapshot.CanEscape);
                Assert.False(snapshot.MainReady);
            }
        }
        Assert.Null(System.Windows.Application.Current);
    }
}
