using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedS54TimeUiTests
{
    [OfflineNativeDecisionFact]
    public void Disabled1201ClockHasPositivePageIdentityAndCanReturnToMain()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var path = Environment.GetEnvironmentVariable("BGI_S54_TIME_FRAME");
        Assert.True(File.Exists(path));
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        var clock = new FakeTimeProvider();
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0) { FrameStamp = new CaptureFrameSource(clock).Next() };
        var page = TimeSettingUiReader.Read(frame, ocr);
        Assert.True(page.Visible);
        Assert.Equal(721, page.CurrentMinutes);
        Assert.Equal(721, page.SelectedMinutes);
        Assert.True(page.TooClose);
        var revive = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr, "复苏", "使用道具复苏角色");
        var snapshot = NativeUiDriver.Read(frame, ocr: ocr, reviveDetector: revive,
            domainTipTexts: DomainTipUiReaderTests.Texts, clock: clock);
        Assert.True(snapshot.TimeSetting);
        Assert.True(snapshot.CanEscape);
        Assert.False(snapshot.MainReady);
        Assert.False(snapshot.CanDismissDomainTip);
        var handbook = Environment.GetEnvironmentVariable("BGI_S53_HANDBOOK_FRAME");
        var world = Environment.GetEnvironmentVariable("BGI_S51_HUD_FRAMES")?.Split('|')[0];
        foreach (var negative in new[] { handbook, world, Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "food-revive-controller-20260910.png") })
        {
            Assert.True(File.Exists(negative));
            using var other = new ImageRegion(Cv2.ImRead(negative!), 0, 0);
            Assert.False(TimeSettingUiReader.Read(other, ocr).Visible);
        }
    }
}
