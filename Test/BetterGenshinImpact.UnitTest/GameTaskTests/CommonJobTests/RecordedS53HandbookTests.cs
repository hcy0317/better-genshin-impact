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
public class RecordedS53HandbookTests
{
    [OfflineNativeDecisionFact]
    public void ActualS53FrameIsAHandbookCloseProposalNotMainHud()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var path = Environment.GetEnvironmentVariable("BGI_S53_HANDBOOK_FRAME");
        Assert.True(File.Exists(path), "需要显式本地实拍图，不提交私人完整截图");
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        var clock = new FakeTimeProvider(); // 只验证真实像素分类，耗时合同另由driver时钟回放验证。
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0) { FrameStamp = new CaptureFrameSource(clock).Next() };
        var revive = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr, "复苏", "使用道具复苏角色");
        var snapshot = NativeUiDriver.Read(frame, ocr: ocr, reviveDetector: revive,
            domainTipTexts: DomainTipUiReaderTests.Texts, clock: clock);
        Assert.True(snapshot.Handbook);
        Assert.True(snapshot.CanEscape);
        Assert.False(snapshot.MainReady);
        Assert.False(snapshot.CanDismissDomainTip);
        Assert.Null(System.Windows.Application.Current);
    }
}
