using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class CombatMotionReaderTests
{
    [OfflineNativeDecisionFact]
    public void RecordedS26FreezeCannotBeClassifiedAsNormalFromTheVisibleSkillLabels()
    {
        Assert.Null(System.Windows.Application.Current);
        var path = Environment.GetEnvironmentVariable("BGI_RECORDED_FREEZE_FRAME");
        Assert.True(!string.IsNullOrEmpty(path) && File.Exists(path), "需要显式提供S26录制截图，不能用合成图替代");
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var frozen = new ImageRegion(Cv2.ImRead(path!), 0, 0);
        Assert.NotEqual(MotionStatus.Normal, CombatMotionReader.Read(frozen, true, ocr));
        Assert.True(CombatMotionReader.ReadControl(frozen, true, ocr).KeyboardBreakoutRequested);
        Assert.Null(System.Windows.Application.Current);
    }

    [OfflineNativeDecisionFact]
    public void SkillLabelsAloneDoNotProveNormalMotionOrAuthorizeBreakout()
    {
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var ground = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "inactive-zhongli-20260911.png")), 0, 0);
        using var blank = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        Assert.Equal(MotionStatus.Unknown, CombatMotionReader.Read(ground, true, ocr));
        Assert.False(CombatMotionReader.ReadControl(ground, true, ocr).KeyboardBreakoutRequested);
        Assert.Equal(MotionStatus.Unknown, CombatMotionReader.Read(blank, true, ocr));
        Assert.Equal(MotionStatus.Unknown, CombatMotionReader.Read(ground, false, ocr));
    }
}
