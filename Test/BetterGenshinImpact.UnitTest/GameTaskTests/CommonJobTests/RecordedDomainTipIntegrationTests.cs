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
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedDomainTipIntegrationTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public void FullRecordedFrameUsesProductionSceneAndDomainTipReadersTogether()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        var path = Environment.GetEnvironmentVariable("BGI_RECORDED_DOMAIN_TIP_FRAME");
        Assert.True(!string.IsNullOrWhiteSpace(path) && File.Exists(path), "Explicit local recording required; do not commit private screenshots.");
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        var clock = new FakeTimeProvider(); // 本测试验证同帧分类组合，不作为延迟性能证明。
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0) { FrameStamp = new CaptureFrameSource(clock).Next() };
        Assert.False(frame.SrcMat.Empty());
        var revive = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr, "复苏", "使用道具复苏角色");
        var snapshot = NativeUiDriver.Read(frame, ocr: ocr, reviveDetector: revive,
            domainTipTexts: DomainTipUiReaderTests.Texts, clock: clock);
        output.WriteLine(snapshot.Describe());
        Assert.True(snapshot.DomainTip.CanDismiss);
        Assert.True(snapshot.CanDismissDomainTip, snapshot.Describe());
        Assert.Null(System.Windows.Application.Current);
    }
}
