using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using OpenCvSharp;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathMoveToClimbTests
{
    [Fact]
    public async Task FiveMillisecondPollingPreservesWindowAcrossTwentyFpsDuplicateFrames()
    {
        var replay = new PathReplay { ProducerIntervalMilliseconds = 50 };
        replay.Executor.PartyConfig.HurryOnFrameInterval = 5;
        replay.PositionAt = _ => replay.Inputs.Any(x => x.Action == GIActions.Drop)
            ? new Point2f(100, 100) : new Point2f(90, 100);

        await replay.Executor.MoveTo(replay.Point("climb"));

        var firstDrop = replay.Inputs.First(x => x.Action == GIActions.Drop);
        Assert.InRange((firstDrop.At - replay.Started).TotalSeconds, 9, 12);
        Assert.Contains(replay.Inputs, x => x.Action == GIActions.Jump);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task DeclaredClimbWithFreshNormalStallUsesExistingRecovery()
    {
        var replay = new PathReplay { MinimumWait = 1100 };
        replay.PositionAt = _ => replay.Inputs.Any(x => x.Action == GIActions.Drop) ? new Point2f(100, 100) : new Point2f(90, 100);
        var error = await Record.ExceptionAsync(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.Contains(replay.Inputs, x => x.Action == GIActions.Drop);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("climb")]
    [InlineData("fly")]
    [InlineData("hud")]
    [InlineData("fallback")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("progress")]
    public async Task NonNormalOrNonContinuousProgressNeverEntersRecovery(string kind)
    {
        var replay = new PathReplay { MinimumWait = 1100 };
        replay.PositionAt = frame => new Point2f(kind == "progress" ? 90 - frame : 90, 100);
        replay.MotionAt = _ => kind == "climb" ? MotionStatus.Climb : kind == "fly" ? MotionStatus.Fly : MotionStatus.Normal;
        replay.BeforeCapture = frame => { replay.Hud = kind != "hud" || frame % 8 != 0; replay.Direct = kind != "fallback" || frame % 8 != 0; };
        if (kind == "unknown") replay.StampTransform = stamp => replay.Frames % 8 == 0 ? default : stamp;
        if (kind == "duplicate") replay.Duplicate = true;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Drop);
    }

    [Fact]
    public async Task IndependentRecheckBecomingClimbDoesNotStartRecovery()
    {
        var replay = new PathReplay { MinimumWait = 1100, PositionAt = _ => new Point2f(90, 100) };
        replay.MotionAt = frame => frame >= 12 ? MotionStatus.Climb : MotionStatus.Normal;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Drop);
    }

    [Theory]
    [InlineData("climb")]
    [InlineData("fly")]
    [InlineData("hud")]
    [InlineData("fallback")]
    public async Task ControlChangesDuringHeldRecoveryReleaseBeforeAnyFurtherRecoveryInput(string reason)
    {
        var replay = new PathReplay { MinimumWait = 1100, PositionAt = _ => new Point2f(90, 100) };
        GIActions? held = null;
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.Drop) replay.MinimumWait = 0;
            if (type != KeyType.KeyDown || action == GIActions.MoveForward) return;
            held = action;
            replay.MotionAt = _ => reason == "fly" ? MotionStatus.Fly : reason == "climb" ? MotionStatus.Climb : MotionStatus.Normal;
            replay.Hud = reason != "hud";
            replay.Direct = reason != "fallback";
        };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.NotNull(held);
        var down = replay.Inputs.FindIndex(x => x.Action == held && x.Type == KeyType.KeyDown);
        Assert.Equal((held.Value, KeyType.KeyUp), (replay.Inputs[down + 1].Action, replay.Inputs[down + 1].Type));
        Assert.DoesNotContain(replay.Inputs.Skip(down + 1), x => x.Action is GIActions.Drop or GIActions.Jump);
        Assert.Empty(replay.MouseInputs);
    }

    [Theory]
    [InlineData("drop")]
    [InlineData("settle")]
    [InlineData("held")]
    [InlineData("jump")]
    [InlineData("camera")]
    public async Task OriginalSixtySecondBudgetStopsEachRecoveryWaitPhase(string phase)
    {
        var replay = new PathReplay { MinimumWait = 1100, PositionAt = _ => new Point2f(90, 100) };
        DateTimeOffset? dropped = null;
        if (phase == "camera") replay.CameraAngle = 17;
        var expired = false;
        replay.OnInput = (action, type) => { if (action == GIActions.Drop && type == KeyType.KeyPress) { dropped ??= replay.Clock.GetUtcNow(); replay.MinimumWait = 0; } };
        replay.AfterDelay = _ =>
        {
            if (dropped == null || expired) return;
            var held = replay.Inputs.Any(x => x.Type == KeyType.KeyDown && x.Action != GIActions.MoveForward);
            var jump = replay.Inputs.Any(x => x.Action == GIActions.Jump);
            var ready = phase switch
            {
                "drop" => true,
                "settle" => (replay.Clock.GetUtcNow() - dropped.Value).TotalMilliseconds > 75,
                "held" => held,
                "jump" => jump,
                "camera" => replay.MouseInputs.Count > 0,
                _ => false
            };
            if (ready) { expired = true; replay.Clock.Advance(replay.Started.AddSeconds(60) - replay.Clock.GetUtcNow()); }
        };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.True(expired);
        Assert.All(replay.Inputs.Where(x => x.At >= replay.Started.AddSeconds(60)), x => Assert.Equal(KeyType.KeyUp, x.Type));
        Assert.All(replay.MouseInputs, x => Assert.True(x.At < replay.Started.AddSeconds(60)));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task RecoveryStartedAtFiftyNinePointNineDoesNotReceiveANewBudget()
    {
        var replay = new PathReplay { MinimumWait = 1100, PositionAt = _ => new Point2f(90, 100) };
        replay.BeforeCapture = frame => { if (frame == 12) { replay.Clock.Advance(replay.Started.AddSeconds(59.9) - replay.Clock.GetUtcNow()); replay.MinimumWait = 0; } };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.Contains(replay.Inputs, x => x.Action == GIActions.Drop);
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.Jump);
        Assert.All(replay.Inputs.Where(x => x.At >= replay.Started.AddSeconds(60)), x => Assert.Equal(KeyType.KeyUp, x.Type));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualManagedFocusWaitUsesSameRemainingNodeBudget(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token) { MinimumWait = 1100, PositionAt = _ => new Point2f(90, 100) };
        replay.OnCheckInput = () =>
        {
            Assert.NotNull(UiOperation.Current);
            Assert.True(UiOperation.Current.Remaining < TimeSpan.FromSeconds(51));
            TaskControl.WaitWhileManaged(() => true, () => { }, milliseconds =>
            {
                replay.Clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                if (cancel) cancellation.Cancel();
            });
        };
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        else await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.Drop);
        Assert.Null(UiOperation.Current);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }

    [Fact]
    public async Task NormalClimbRecoveryAdvancesWithNonBlockingTwentyFpsCapture()
    {
        var replay = new PathReplay { MinimumWait = 1100, ProducerIntervalMilliseconds = 50 };
        replay.PositionAt = _ => replay.Inputs.Any(x => x.Action == GIActions.Drop) ? new Point2f(100, 100) : new Point2f(90, 100);
        replay.OnInput = (action, _) => { if (action == GIActions.Drop) replay.MinimumWait = 0; };
        await replay.Executor.MoveTo(replay.Point("climb"));
        Assert.Contains(replay.Inputs, x => x.Action == GIActions.Drop);
        Assert.Contains(replay.Inputs, x => x.Action == GIActions.Jump);
    }

    [Fact]
    public async Task FrozenProducerDuringHeldRecoveryCannotHoldIndefinitely()
    {
        var replay = new PathReplay { MinimumWait = 1100, ProducerIntervalMilliseconds = 50, PositionAt = _ => new Point2f(90, 100) };
        GIActions? held = null;
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.Drop) replay.MinimumWait = 0;
            if (type == KeyType.KeyDown && action != GIActions.MoveForward) { held = action; replay.Duplicate = true; }
        };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.NotNull(held);
        var down = replay.Inputs.FindIndex(x => x.Action == held && x.Type == KeyType.KeyDown);
        var up = replay.Inputs[down + 1];
        Assert.Equal((held.Value, KeyType.KeyUp), (up.Action, up.Type));
        Assert.InRange((up.At - replay.Inputs[down].At).TotalSeconds, 0, 2.1);
        Assert.DoesNotContain(replay.Inputs.Skip(down + 1), x => x.Action is GIActions.Drop or GIActions.Jump);
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("locate")]
    [InlineData("native")]
    [InlineData("cancel")]
    public async Task RecoveryFailureDoesNotLeakItsHeldKey(string failure)
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token) { MinimumWait = 1100, PositionAt = _ => new Point2f(90, 100) };
        GIActions? held = null;
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.Drop) replay.MinimumWait = 0;
            if (type != KeyType.KeyDown || action == GIActions.MoveForward) return;
            held = action;
            if (failure == "native") throw new IOException("native");
            if (failure == "capture") replay.BeforeCapture = _ => throw new IOException("capture");
            if (failure == "locate") replay.PositionAt = _ => throw new IOException("locate");
            if (failure == "cancel") cancellation.Cancel();
        };
        var error = await Record.ExceptionAsync(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.NotNull(error);
        Assert.NotNull(held);
        var down = replay.Inputs.FindIndex(x => x.Action == held && x.Type == KeyType.KeyDown);
        Assert.Equal((held.Value, KeyType.KeyUp), (replay.Inputs[down + 1].Action, replay.Inputs[down + 1].Type));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task RecoveryStillRetriesOnThirdStallRatherThanClaimingArrival()
    {
        var replay = new PathReplay { MinimumWait = 1100, PositionAt = _ => new Point2f(90, 100) };
        replay.OnInput = (action, _) => { if (action == GIActions.Drop) replay.MinimumWait = 0; };
        var error = await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.Contains("3次卡死", error.Message);
        Assert.Equal(2, replay.Inputs.Count(x => x.Type == KeyType.KeyDown && x.Action != GIActions.MoveForward));
    }

    [Fact]
    public async Task CameraCannotExtendInnerFiveSecondRecoveryWindow()
    {
        var replay = new PathReplay { MinimumWait = 1100, LockCamera = true };
        DateTimeOffset? cameraStarted = null;
        replay.PositionAt = _ => cameraStarted != null && replay.Clock.GetUtcNow() - cameraStarted.Value >= TimeSpan.FromSeconds(5)
            ? new Point2f(100, 100) : new Point2f(90, 100);
        replay.ReadCamera = () => { cameraStarted ??= replay.Clock.GetUtcNow(); replay.Clock.Advance(TimeSpan.FromMilliseconds(200)); return 17; };
        replay.OnInput = (action, _) => { if (action == GIActions.Drop) replay.MinimumWait = 0; };
        await replay.Executor.MoveTo(replay.Point("climb"));
        Assert.NotNull(cameraStarted);
        Assert.All(replay.MouseInputs, input => Assert.True(input.At - cameraStarted.Value < TimeSpan.FromSeconds(5)));
        Assert.DoesNotContain(replay.RecoveryInputs, input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown);
    }

    [Theory]
    [InlineData("motion")]
    [InlineData("hud")]
    public async Task SlowRecognitionCannotAccumulateClimbRecoveryEvidence(string reader)
    {
        var replay = new PathReplay { PositionAt = _ => new Point2f(90, 100) };
        replay.HudAt = _ => { if (reader == "hud") replay.Clock.Advance(TimeSpan.FromSeconds(2.1)); return true; };
        replay.MotionAt = _ => { if (reader == "motion") replay.Clock.Advance(TimeSpan.FromSeconds(2.1)); return MotionStatus.Normal; };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("climb")));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.Drop);
    }
}
