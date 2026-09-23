using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using BetterGenshinImpact.Core.Recognition.OCR;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedS55DomainExitTests
{
    [OfflineNativeDecisionFact]
    public void RecordedExitPromptIsNotAnUnrecoverableBlackButton()
    {
        var path = Environment.GetEnvironmentVariable("BGI_S55_EXIT_FRAME");
        Assert.True(File.Exists(path));
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        var clock = new FakeTimeProvider();
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0) { FrameStamp = new CaptureFrameSource(clock).Next() };
        var revive = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr, "复苏", "使用道具复苏角色");
        var observed = NativeUiDriver.Read(frame, ocr: ocr, reviveDetector: revive, clock: clock);
        Assert.True(observed.InDomain);
        Assert.True(observed.BlackConfirm);
        Assert.False(observed.MainReady);
        Assert.True(observed.CanEscape);
        Assert.True(observed.CanConfirmDomainExit);
        foreach (var name in new[] { "food-revive-controller-20260910.png", "s32-food-revive-20260916.png", "s32-mining-hud-20260916.png", "s32-map-marker-20260916.png" })
        {
            using var other = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", name)), 0, 0)
            { FrameStamp = new CaptureFrameSource(clock).Next() };
            Assert.False(DomainExitUiReader.Read(other, ocr).Visible, name);
        }
        foreach (var fault in new[] { "resin", "title", "missing", "duplicate", "bounds", "nan", "tiny" })
        {
            var calls = 0;
            var fake = new DomainTipUiReaderTests.BoxesOcr { Read = mat =>
            {
                var index = calls++;
                var text = new[] { "提示", "退出秘境", "取消", "确认" }[index];
                if (fault == "resin" && index == 1) text = "使用原粹树脂";
                if (fault == "title" && index == 0) text = "复苏";
                if (fault == "missing" && index == 1) return new([]);
                var box = new RotatedRect(new Point2f(mat.Width / 2, mat.Height / 2),
                    fault == "tiny" ? new Size2f(1, 1) : new Size2f(80, 20), 0);
                if (fault == "bounds") box.Center = new(-5, 20);
                if (fault == "nan") box.Center = new(float.NaN, 20);
                var region = new OcrResultRegion(box, text, 1);
                return fault == "duplicate" ? new([region, region]) : new([region]);
            } };
            Assert.False(DomainExitUiReader.Read(frame, fake).Visible, fault);
        }
    }
}
