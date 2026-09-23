using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedS51HudProbeTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public void RecordedNormalHudAndTransformedHudKeepSeparateAdmission()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var paths = Environment.GetEnvironmentVariable("BGI_S51_HUD_FRAMES")?.Split('|') ?? [];
        Assert.Equal(4, paths.Length); // 三张S51普通角色原帧，最后一张为619变身原帧。
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        for (var i = 0; i < paths.Length; i++)
        {
            var path = paths[i];
            Assert.True(File.Exists(path));
            using var frame = new ImageRegion(Cv2.ImRead(path), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
            var confirm = RecognitionAssets.Get("AutoFight", "Confirm", frame);
            var detector = new ReviveUiDetector(confirm, ocr, "复苏", "使用道具复苏角色");
            using var found = frame.Find(confirm);
            using var paimon = frame.Find(ElementRecognition.Get("PaimonMenu", frame));
            using var chat = frame.Find(ElementRecognition.Get("FriendChat", frame));
            output.WriteLine($"{Path.GetFileName(path)} hud={detector.IsCombatHud(frame)} confirm={found.IsExist()} paimon={paimon.IsExist()} chat={chat.IsExist()} motion={Bv.GetMotionStatus(frame)}");
            Assert.Equal(i < 3, detector.IsCombatHud(frame));
            using var darkPixels = new Mat();
            frame.SrcMat.ConvertTo(darkPixels, frame.SrcMat.Type(), .7);
            using var dark = new ImageRegion(darkPixels.Clone(), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
            Assert.False(new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", dark), ocr, "复苏", "使用道具复苏角色").IsCombatHud(dark));
            if (chat.IsExist())
            {
                using var area = frame.DeriveCrop(new Rect(chat.X, chat.Y, chat.Width, chat.Height));
                foreach (var threshold in new[] { 235, 220, 200, 180 })
                {
                    using var mask = new Mat();
                    Cv2.InRange(area.SrcMat, new Scalar(threshold, threshold, threshold, 0), Scalar.All(255), mask);
                    output.WriteLine($"chat bounds={chat.X},{chat.Y},{chat.Width},{chat.Height} threshold={threshold} white={Cv2.CountNonZero(mask)} required={Math.Max(4, chat.Width * chat.Height / 20)}");
                }
            }
        }
        foreach (var file in new[] { "food-revive-controller-20260910.png", "s32-food-revive-20260916.png" })
        {
            using var frame = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", file)), 0, 0);
            Assert.False(frame.SrcMat.Empty());
            Assert.False(new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr, "复苏", "使用道具复苏角色").IsCombatHud(frame));
        }
        Assert.Null(System.Windows.Application.Current);
    }
}
