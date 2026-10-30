using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class PartyRejectionScreenshotTests
{
    [UiRecoveryTheory("s17-party-rejection-feedback-20261030.png")]
    [InlineData(false)]
    [InlineData(true)]
    public void ActualPartyRejectionIsObservedOnlyWhileItsCaptionIsPresent(bool eraseCaption)
    {
        Assert.True(BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.IsProhibited);
        var bytes = File.ReadAllBytes(UiRecoveryFixtures.PathFor("s17-party-rejection-feedback-20261030.png"));
        Assert.Equal("E8A1A4F133A31EA29CC5438560FFD92D72CFE690AE30D9C9EB823974D9CAFCC9",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var pixels = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (eraseCaption)
        {
            using var caption = new Mat(pixels, new Rect(700, 180, 550, 75));
            caption.SetTo(Scalar.Black);
        }
        // 回放源只用于离线场景判定，不冒充历史输入完成时刻。
        using var image = new ImageRegion(pixels.Clone(), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var detector = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", image),
            ocr, "复苏", "使用道具复苏角色");
        var observed = NativeUiDriver.Read(image, inspectWorld: true, ocr: ocr, reviveDetector: detector);
        Assert.True(observed.MainHud);
        Assert.NotNull(observed.World);
        Assert.Equal<bool?>(!eraseCaption, observed.World?.PartyRejected);
        Assert.False(observed.Matches(UiTarget.Party));
        Assert.False(image.SrcMat.Empty());
        Assert.Null(System.Windows.Application.Current);
    }
}
