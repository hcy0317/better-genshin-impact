using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using OpenCvSharp;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedSaurianSceneTests
{
    [OfflineNativeDecisionFact]
    public async Task FarLocationRecoveryReleasesTailBeforeAnyRecoveryUiInput()
    {
        using var pixels = Cv2.ImRead(Environment.GetEnvironmentVariable("BGI_SAURIAN_PROBE_FRAMES")!.Split('|')[0]);
        var io = new LegacyPathingMacroTests.MacroReplay();
        using var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        var recoveryReached = false;
        var pathIo = new PathMoveToIo
        {
            Clock = io.Time, LoggerFactory = () => NullLogger.Instance,
            Capture = () => new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = io.NextFrame() },
            Locate = (_, _) => Task.FromResult(new PathPosition(new(1500,1500), 0, true)),
            SwitchAvatar = _ => throw new Exception("变身导航不得切人"),
            EndJudgment = _ => { }, RotateUntil = (_, _) => Task.FromResult(true),
            Motion = _ => MotionStatus.Normal, CombatHud = _ => false,
            IsDown = _ => session.HasTail, Delay = io.Delay,
            Send = (_, type) => Assert.Equal(BetterGenshinImpact.Core.Simulator.Extensions.KeyType.KeyUp, type),
            RecoverUi = (_, _) =>
            {
                recoveryReached = true;
                Assert.False(session.HasTail);
                Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
                using var successor = io.Coordinator.TryAcquire(Guid.NewGuid(), () => { });
                Assert.NotNull(successor);
                throw new OperationCanceledException("结束回放，不执行真实UI操作");
            }
        };
        var executor = new PathExecutor(default, pathIo, session) { PartyConfig = new PathingPartyConfig { MainAvatarIndex = "1" } };
        var waypoint = new WaypointForTrack(new Waypoint { X=100,Y=100,MoveMode="walk",Type="path" },
            new RouteMapContext("Teyvat", "SIFT", null), point => point) { X=100,Y=100 };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executor.MoveTo(waypoint));
        Assert.True(recoveryReached);
    }

    [OfflineNativeDecisionFact]
    public async Task ProductionMoveToAcceptsRecordedTransformationAndRetiresTailWithoutSwitching()
    {
        var path = Environment.GetEnvironmentVariable("BGI_SAURIAN_PROBE_FRAMES")!.Split('|')[0];
        using var pixels = Cv2.ImRead(path);
        var io = new LegacyPathingMacroTests.MacroReplay();
        using var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        var images = new List<ImageRegion>();
        var pathIo = new PathMoveToIo
        {
            Clock = io.Time, LoggerFactory = () => NullLogger.Instance,
            Capture = () => { var frame = new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = io.NextFrame() }; images.Add(frame); return frame; },
            Locate = (_, _) => Task.FromResult(new PathPosition(new(100,100), 0, true)),
            SwitchAvatar = _ => throw new Exception("变身导航不得切人"),
            EndJudgment = _ => { }, RotateUntil = (_, _) => Task.FromResult(true),
            Motion = _ => MotionStatus.Normal, CombatHud = _ => false,
            Send = (_, _) => throw new Exception("持有的W不能重复按下或重复松开")
        };
        var executor = new PathExecutor(default, pathIo, session) { PartyConfig = new PathingPartyConfig { MainAvatarIndex = "1" } };
        var waypoint = new WaypointForTrack(new Waypoint { X=100,Y=100,MoveMode="walk",Type="path" },
            new RouteMapContext("Teyvat", "SIFT", null), point => point) { X=100,Y=100 };
        await executor.MoveTo(waypoint);
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
        Assert.False(session.HasTail);
        Assert.All(images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [OfflineNativeDecisionFact]
    public void RecordedTransformationRequiresAllIndependentHudEvidence()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var paths = Environment.GetEnvironmentVariable("BGI_SAURIAN_PROBE_FRAMES")?.Split('|') ?? [];
        Assert.Equal(5, paths.Length);
        for (var i = 0; i < paths.Length; i++)
        {
            using var frame = new ImageRegion(Cv2.ImRead(paths[i]), 0, 0);
            Assert.False(frame.SrcMat.Empty());
            Assert.Equal(i < 3, SaurianUiReader.IsKnownTransformation(frame));
            using var dark = new Mat();
            frame.SrcMat.ConvertTo(dark, frame.SrcMat.Type(), .7);
            using var dimFrame = new ImageRegion(dark.Clone(), 0, 0);
            Assert.False(SaurianUiReader.IsKnownTransformation(dimFrame));
        }
        foreach (var file in new[] { "food-revive-controller-20260910.png", "s32-food-revive-20260916.png" })
        {
            using var frame = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", file)), 0, 0);
            Assert.False(frame.SrcMat.Empty());
            Assert.False(SaurianUiReader.IsKnownTransformation(frame));
        }
    }
}
