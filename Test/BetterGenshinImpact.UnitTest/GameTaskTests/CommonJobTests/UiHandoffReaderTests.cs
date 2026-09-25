using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class UiHandoffReaderTests
{
    [OfflineNativeDecisionFact]
    public void RecordedS61WorldFramesDistinguishTransformationOrdinaryAndUnknown()
    {
        var paths = Environment.GetEnvironmentVariable("BGI_S61_HANDOFF_FRAMES")?.Split('|') ?? [];
        Assert.Equal(3, paths.Length);
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        for (var index = 0; index < paths.Length; index++)
        {
            using var frame = new ImageRegion(Cv2.ImRead(paths[index]), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
            Assert.False(frame.SrcMat.Empty());
            var revive = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr, "复苏", "使用道具复苏角色");
            var snapshot = NativeUiDriver.Read(frame, inspectWorld: true, ocr: ocr, reviveDetector: revive);
            Assert.True((index == 1) == (snapshot.World?.OrdinaryAvatarHud == true), $"index={index} {snapshot.Describe()}");
            Assert.True((index == 0) == (snapshot.World is { Transformed: true } ||
                snapshot.World is { Motion: MotionStatus.Fly }), $"index={index} {snapshot.Describe()}");
            if (index < 2) Assert.True(snapshot.World?.ControlObserved);
            if (index == 2)
                Assert.Equal(BetterGenshinImpact.GameTask.AutoFight.Script.Flow.PathingMacroScene.Unknown,
                    BetterGenshinImpact.GameTask.AutoFight.Script.Flow.NativePathingMacroIo.ReadScene(frame, ocr, revive).Scene);
        }
        Assert.Null(System.Windows.Application.Current);
    }
}
