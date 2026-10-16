using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedNoonSaurianTests
{
    [UiRecoveryFact("saurian-1610-blocked.png")]
    public async Task RecordedAfternoonDashUsesNativeTransformationReaderThroughTheProductionApproach()
    {
        using var pixels = UiRecoveryFixtures.ReadRecordedFrame("saurian-1610-blocked.png");
        var replay = new AutoPathingTests.PathReplay { EmitReceipts = true, Hud = false,
            FramePixels = () => pixels.Clone(), ReadTransformation = SaurianUiReader.IsKnownTransformation,
            MotionAt = _ => MotionStatus.Fly };
        var released = false;
        replay.PositionAt = _ => released ? new Point2f(100, 100) : new Point2f(104, 100);
        replay.OnInput = (action, type) =>
        {
            if (action == BetterGenshinImpact.Core.Simulator.Extensions.GIActions.MoveForward &&
                type == BetterGenshinImpact.Core.Simulator.Extensions.KeyType.KeyUp) released = true;
        };
        await replay.Executor.MoveCloseTo(replay.Point("dash"));
        Assert.Single(replay.Inputs.Where(input => input.Type == BetterGenshinImpact.Core.Simulator.Extensions.KeyType.KeyDown));
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [UiRecoveryTheory("saurian-1610-blocked.png", "saurian-1610-recovered.png", "saurian-1610-approach.png",
        "saurian-1610-before.png", "saurian-1610-after.png")]
    [InlineData("saurian-1610-blocked.png")]
    [InlineData("saurian-1610-recovered.png")]
    [InlineData("saurian-1610-approach.png")]
    [InlineData("saurian-1610-before.png")]
    [InlineData("saurian-1610-after.png")]
    public void AfternoonTransformationDoesNotBecomeUnknownWhenItsSkillIconChanges(string name)
    {
        using var frame = new ImageRegion(UiRecoveryFixtures.ReadRecordedFrame(name), 0, 0);
        Assert.True(SaurianUiReader.IsKnownTransformation(frame), SaurianUiReader.Describe(frame));
        foreach (var bounds in new[] { new Rect(1785, 965, 65, 60), new Rect(1260, 950, 515, 80),
                     new Rect(805, 1005, 310, 15), new Rect(0, 0, 100, 1080) })
        {
            using var missing = new ImageRegion(frame.SrcMat.Clone(), 0, 0);
            using var area = new Mat(missing.SrcMat, bounds);
            area.SetTo(Scalar.Black);
            Assert.False(SaurianUiReader.IsKnownTransformation(missing), "missing=" + bounds + " " + SaurianUiReader.Describe(missing));
        }
    }

    [UiRecoveryTheory("saurian-entry-20261016.png", "saurian-post-20261016.png")]
    [InlineData("saurian-entry-20261016.png")]
    [InlineData("saurian-post-20261016.png")]
    public void SameRunRawMacroFramesRemainRecognizedAfterCameraMovement(string name)
    {
        using var frame = new ImageRegion(UiRecoveryFixtures.ReadRecordedFrame(name), 0, 0);
        Assert.True(SaurianUiReader.IsKnownTransformation(frame), SaurianUiReader.Describe(frame));
        foreach (var bounds in new[] { new Rect(1785, 965, 65, 60), new Rect(1680, 955, 75, 70),
                     new Rect(805, 1005, 310, 15), new Rect(0, 0, 100, 1080) })
        {
            using var missing = new ImageRegion(frame.SrcMat.Clone(), 0, 0);
            using var area = new Mat(missing.SrcMat, bounds);
            area.SetTo(Scalar.Black);
            Assert.False(SaurianUiReader.IsKnownTransformation(missing), "missing=" + bounds);
        }
        using var darkPixels = new Mat();
        frame.SrcMat.ConvertTo(darkPixels, frame.SrcMat.Type(), .7);
        using var dark = new ImageRegion(darkPixels.Clone(), 0, 0);
        Assert.False(SaurianUiReader.IsKnownTransformation(dark));
    }

    [UiRecoveryFact("saurian-entry-20261016.png", "saurian-post-20261016.png")]
    public async Task RecordedMacroUsesNativeSceneReaderAndCompletesWithoutReplayingInputs()
    {
        using var entry = UiRecoveryFixtures.ReadRecordedFrame("saurian-entry-20261016.png");
        using var after = UiRecoveryFixtures.ReadRecordedFrame("saurian-post-20261016.png");
        var io = new RecordedIo(entry, after);
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            "mousedown,wait(.5),moveby(-150,3000),wait(.2),moveby(0,1000),wait(.5),mouseup", false), ["钟离"]),
            (_, _) => throw new Exception("must not enter humanoid runner"), default);
        Assert.True(result.CanContinue);
        Assert.Equal(4, io.Boundary.Inputs.Count);
        Assert.False(session.HasTail);
    }

    private sealed class RecordedIo(Mat entry, Mat after) : IPathingMacroIo
    {
        internal LegacyPathingMacroTests.MacroReplay Boundary { get; } = new();
        public TimeProvider Clock => Boundary.Clock;
        public CombatInputCoordinator Coordinator => Boundary.Coordinator;
        public PathingMacroObservation Observe(string phase = "boundary")
        {
            using var frame = new ImageRegion((phase == "entry" ? entry : after).Clone(), 0, 0)
            { FrameStamp = Boundary.NextFrame() };
            return NativePathingMacroIo.ReadScene(frame, new NoOcr());
        }
        public User32.VK Map(User32.VK key) => Boundary.Map(key);
        public CombatBattleHostInputResult Send(PathingMacroInput input, Action admit) => Boundary.Send(input, admit);
        public Task Delay(int milliseconds, CancellationToken ct) => Boundary.Delay(milliseconds, ct);
    }
    private sealed class NoOcr : IOcrService
    {
        public string OcrWithoutDetector(Mat mat) => throw new Exception("transformation should not probe cannon OCR");
        public string Ocr(Mat mat) => throw new NotSupportedException();
        public OcrResult OcrResult(Mat mat) => throw new NotSupportedException();
    }
}
