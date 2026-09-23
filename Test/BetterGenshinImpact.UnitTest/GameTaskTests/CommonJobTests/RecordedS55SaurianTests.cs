using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Xunit.Abstractions;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedS55SaurianTests(ITestOutputHelper output)
{
    [OfflineNativeDecisionFact]
    public async Task RecordedLayoutsPassRawBoundaryAndNavigationWithoutActorSelectionOrReplay()
    {
        var paths = Environment.GetEnvironmentVariable("BGI_S55_SAURIAN_FRAMES")!.Split('|');
        foreach (var path in paths)
        {
            using var pixels = Cv2.ImRead(path);
            var io = new RecordedMacroIo(pixels);
            using var session = new PathingMacroSession(io);
            var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
                CombatScriptParser.ParseContext("keydown(w),wait(0.1)", false), ["琴"]),
                (_, _) => throw new Exception("变身物理宏不能切人"), default);
            Assert.True(result.CanContinue);
            session.AdoptNavigation(io.Observe(), validPosition: true, default);
            Assert.True(session.TryNavigationForward(down: false, default));
            Assert.False(session.HasTail);
            Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Boundary.Inputs);
        }
    }

    private sealed class RecordedMacroIo(Mat pixels) : IPathingMacroIo
    {
        internal LegacyPathingMacroTests.MacroReplay Boundary { get; } = new();
        public TimeProvider Clock => Boundary.Clock;
        public CombatInputCoordinator Coordinator => Boundary.Coordinator;
        public PathingMacroObservation Observe(string phase = "boundary")
        {
            using var frame = new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = Boundary.NextFrame() };
            return new(SaurianUiReader.IsKnownTransformation(frame) ? PathingMacroScene.Transformed : PathingMacroScene.Unknown, frame.FrameStamp);
        }
        public User32.VK Map(User32.VK key) => Boundary.Map(key);
        public CombatBattleHostInputResult Send(PathingMacroInput input, Action admit) => Boundary.Send(input, admit);
        public Task Delay(int milliseconds, CancellationToken ct) => Boundary.Delay(milliseconds, ct);
    }

    [OfflineNativeDecisionFact]
    public void TwoNewLayoutsRequireExitAndTheirOwnAbilityAlongsideWorldAndOrangeHp()
    {
        var paths = Environment.GetEnvironmentVariable("BGI_S55_SAURIAN_FRAMES")?.Split('|') ?? [];
        Assert.Equal(4, paths.Length);
        for (var i = 0; i < paths.Length; i++)
        {
            using var frame = new ImageRegion(Cv2.ImRead(paths[i]), 0, 0);
            Assert.False(frame.SrcMat.Empty());
            Assert.True(SaurianUiReader.IsKnownTransformation(frame), paths[i]);
            foreach (var roi in new[] { new Rect(805, 1005, 310, 15), new Rect(1785, 965, 65, 60),
                i % 2 == 0 ? new Rect(1570, 955, 75, 70) : new Rect(1680, 955, 75, 70),
                new Rect(0, 0, 100, 1080) })
            {
                using var missing = new ImageRegion(frame.SrcMat.Clone(), 0, 0);
                using var area = new Mat(missing.SrcMat, roi);
                area.SetTo(Scalar.Black);
                Assert.False(SaurianUiReader.IsKnownTransformation(missing), $"missing {roi}: {paths[i]}");
            }
            using var dark = new Mat();
            frame.SrcMat.ConvertTo(dark, frame.SrcMat.Type(), .7);
            using var darkFrame = new ImageRegion(dark.Clone(), 0, 0);
            Assert.False(SaurianUiReader.IsKnownTransformation(darkFrame));
        }
        foreach (var name in new[] { "food-revive-controller-20260910.png", "s32-food-revive-20260916.png", "s32-mining-hud-20260916.png", "s32-map-marker-20260916.png" })
        {
            using var frame = new ImageRegion(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", name)), 0, 0);
            Assert.False(SaurianUiReader.IsKnownTransformation(frame), name);
        }
    }

    [OfflineNativeDecisionFact]
    public void EmitDeterministicS55AbilityTemplatesWhenExplicitlyRequested()
    {
        if (Environment.GetEnvironmentVariable("BGI_S55_EMIT_TEMPLATES") != "1") return;
        var paths = Environment.GetEnvironmentVariable("BGI_S55_SAURIAN_FRAMES")!.Split('|');
        foreach (var (index, name, rect) in new[] {
            (0, "Burrow", new Rect(1586, 963, 50, 58)),
            (1, "Spirit", new Rect(1686, 963, 57, 58)) })
        {
            using var pixels = Cv2.ImRead(paths[index]);
            using var crop = new Mat(pixels, rect);
            using var gray = new Mat();
            Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
            output.WriteLine($"S55_TEMPLATE {name}={Convert.ToBase64String(gray.ToBytes())}");
        }
    }
}
