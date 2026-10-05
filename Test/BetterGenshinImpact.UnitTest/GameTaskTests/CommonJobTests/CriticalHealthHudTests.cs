using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class CriticalHealthHudTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("80/46597", true)]
    [InlineData(" 80 / 46597 ", true)]
    [InlineData("0/46597", false)]
    [InlineData("80/0", false)]
    [InlineData("80", false)]
    [InlineData("8O/46597", false)]
    [InlineData("-80/46597", false)]
    [InlineData("46597/46597", false)]
    [InlineData("80/46597/2", false)]
    public void MissingDeadOrNonCriticalNumbersCannotAuthorizeHud(string text, bool expected)
        => Assert.Equal(expected, CriticalHealthHud.IsPositiveCriticalHealth(text));

    [UiRecoveryTheory("critical-hp-empty-20261005.png", "critical-hp-orange-20261005.png", "critical-hp-selection-20261005.png")]
    [InlineData("critical-hp-empty-20261005.png")]
    [InlineData("critical-hp-orange-20261005.png")]
    [InlineData("critical-hp-selection-20261005.png")]
    public void RecordedEightyHpStillHasLivingHudAndMustRemainLowHp(string file)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var frame = new ImageRegion(Cv2.ImRead(UiRecoveryFixtures.PathFor(file)), 0, 0);
        using var roi = new Mat(frame.SrcMat, new Rect(870, 996, 225, 30));
        output.WriteLine("HP=" + ocr.OcrWithoutDetector(roi));
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr,
            "复苏", "使用道具复苏角色");
        Assert.True(detector.IsCombatHud(frame));
        Assert.True(Bv.ObserveCurrentAvatarLowHp(frame));
        if (file.Contains("empty"))
        {
            using var unrelated = new ImageRegion(frame.SrcMat.Clone(), 0, 0);
            Assert.Null(Bv.ObserveCurrentAvatarLowHp(unrelated)); // 数字证据只属于已验证的原区域。
        }
        Assert.Equal(WorldFrameKind.Playable, WorldFrameAvailability.ReadWorld(frame, _ => false,
            detector.IsCombatHud, Bv.IsInBigMapUi, ocr));
        using var dimmed = new Mat();
        frame.SrcMat.ConvertTo(dimmed, frame.SrcMat.Type(), .7);
        using var covered = new ImageRegion(dimmed.Clone(), 0, 0);
        Assert.False(new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", covered), ocr,
            "复苏", "使用道具复苏角色").IsCombatHud(covered));
        Assert.Null(System.Windows.Application.Current);
    }
}
