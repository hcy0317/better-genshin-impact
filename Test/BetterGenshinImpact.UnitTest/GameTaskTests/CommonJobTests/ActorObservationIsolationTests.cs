using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class ActorObservationIsolationTests
{
    [OfflineNativeDecisionFact]
    public void RecordedFrozenLowHealthHudDoesNotAuthorizePartySetup()
    {
        Assert.Null(System.Windows.Application.Current);
        var path = Environment.GetEnvironmentVariable("BGI_RECORDED_UNSAFE_ENTRY_FRAME");
        Assert.True(!string.IsNullOrEmpty(path) && File.Exists(path), "需要S27危险入场录制图");
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var ocr = new PaddleOcrService(factory, PaddleOcrService.PaddleOcrModelType.V6);
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
        var revive = new ReviveUiDetector(RecognitionAssets.Get("AutoFight", "Confirm", frame), ocr,
            "复苏", "使用道具复苏角色");
        var snapshot = NativeUiDriver.Read(frame, inspectWorld: true, ocr, revive);
        Assert.True(snapshot.MainReady); // 不改变全局HUD语义。
        var readiness = snapshot.PartyEntryReadiness();
        Assert.Equal(UiReadinessKind.TemporarilyUnavailable, readiness.Kind);
        Assert.False(readiness.CanProbe);
    }

    [OfflineNativeDecisionFact]
    public void ReadingAnUnknownActorDoesNotRebuildTheSceneLayout()
    {
        Assert.Null(System.Windows.Application.Current);
        Assert.Equal(AppContext.BaseDirectory, Global.StartUpPath);
        var path = Environment.GetEnvironmentVariable("BGI_RECORDED_PARTY_FRAME");
        Assert.True(!string.IsNullOrEmpty(path) && File.Exists(path), "需要显式提供S27录制图");
        using var image = Cv2.ImRead(path!);
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var predictor = factory.CreateYoloPredictor(BgiOnnxModel.BgiAvatarSide);
        using var initial = new ImageRegion(image.Clone(), 0, 0);
        var system = new FakeSystemInfo(new RECT(0, 0, image.Width, image.Height), 1);
        using var scenes = new CombatScenes(predictor, AutoFightAssets.Get(initial), NullLogger.Instance, system);
        scenes.InitializeTeamSilent(initial, new AutoFightConfig { TeamNames = "钟离,娜维娅,班尼特,香菱" });
        Assert.True(scenes.CheckTeamInitialized());
        Assert.Equal(4, PartyAvatarSideIndexHelper.CountIndexRect(initial));
        // 相同错误区域模拟失效布局，四个相同图块不能指认一个“最不同”的角色。
        foreach (var actor in scenes.GetAvatars()) actor.IndexRect = new Rect(400, 900, 16, 17);
        var before = scenes.GetAvatars().Select(actor => actor.IndexRect).ToArray();
        var context = new AvatarActiveCheckContext();
        var source = new CaptureFrameSource();
        for (var index = 0; index < 4; index++)
        {
            using var frame = new ImageRegion(image.Clone(), 0, 0) { FrameStamp = source.Next() };
            _ = scenes.GetActiveAvatarIndex(frame, context);
        }
        Assert.Equal(before, scenes.GetAvatars().Select(actor => actor.IndexRect));
        var generation = scenes.LayoutGeneration;
        using (var prepared = new ImageRegion(image.Clone(), 0, 0) { FrameStamp = source.Next() })
        {
            Assert.Equal(AvatarLayoutPreparation.Changed,
                scenes.PrepareAvatarObservation(prepared, context, TimeSpan.FromSeconds(2)));
            Assert.Equal(generation + 1, scenes.LayoutGeneration);
            Assert.Equal(-1, scenes.LastActiveAvatarIndex); // 找到布局不是确认角色。
            Assert.Equal(AvatarLayoutPreparation.NotRequested,
                scenes.PrepareAvatarObservation(prepared, context, TimeSpan.FromSeconds(2)));
        }
        using (var changed = new ImageRegion(image.Clone(), 0, 0) { FrameStamp = source.Next() })
            Assert.Equal(-2, scenes.GetActiveAvatarIndex(changed, context));
        using (var confirmed = new ImageRegion(image.Clone(), 0, 0) { FrameStamp = source.Next() })
            Assert.Equal(3, scenes.GetActiveAvatarIndex(confirmed, context));
        using (var unbound = new ImageRegion(image.Clone(), 0, 0))
        {
            var unreadable = new AvatarActiveCheckContext { TotalCheckFailedCount = 3 };
            Assert.Equal(AvatarLayoutPreparation.Unavailable,
                scenes.PrepareAvatarObservation(unbound, unreadable, TimeSpan.FromSeconds(2)));
            Assert.Equal(generation + 1, scenes.LayoutGeneration);
        }
        Assert.Null(System.Windows.Application.Current);
    }
}
