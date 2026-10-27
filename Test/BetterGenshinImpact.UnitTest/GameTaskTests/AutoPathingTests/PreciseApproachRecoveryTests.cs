using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using OpenCvSharp;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PreciseApproachRecoveryTests
{
    [Fact]
    public async Task TransformationEnteringWaterDuringRotationCannotSendGroundMovement()
    {
        var rotated = false;
        var replay = new PathReplay { EmitReceipts = true, Transformed = true, Hud = false,
            PositionAt = _ => new Point2f(104, 100),
            MotionAt = _ => rotated ? MotionStatus.Swim : MotionStatus.Fly };
        replay.OnRotateUntil = () => rotated = true;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("dash")));
        Assert.True(rotated);
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
        Assert.Empty(replay.RecoveryInputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransformationDoesNotOverrideSwimmingOrLossOfItsHudAfterRotation(bool losesHud)
    {
        var replay = new PathReplay { EmitReceipts = true, Transformed = true, Hud = false,
            PositionAt = _ => new Point2f(104, 100), MotionAt = _ => losesHud ? MotionStatus.Fly : MotionStatus.Swim };
        replay.OnRotateUntil = () => replay.Transformed = false;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("dash")));
        Assert.DoesNotContain(replay.Inputs, input => input.Type == KeyType.KeyDown);
        Assert.Empty(replay.RecoveryInputs);
    }

    [Theory]
    [InlineData("walk")]
    [InlineData("run")]
    [InlineData("dash")]
    public async Task ConfirmedTransformationCanFinishAuthoredGroundApproachWithoutHumanoidRecovery(string mode)
    {
        // 16:10 run, 620 node 24: transformed UI, dash, legacy motion Fly.
        // Production must consume its direct position, not wait for a humanoid HUD.
        var moved = false;
        var replay = new PathReplay { EmitReceipts = true, Transformed = true, Hud = false,
            MotionAt = _ => MotionStatus.Fly };
        replay.PositionAt = _ => moved ? new Point2f(100, 100) : new Point2f(104, 100);
        replay.OnInput = (action, type) =>
        { if (action == GIActions.MoveForward && type == KeyType.KeyUp && replay.Inputs.Any(i => i.Type == KeyType.KeyDown)) moved = true; };
        await replay.Executor.MoveCloseTo(replay.Point(mode));
        Assert.True(moved);
        Assert.Single(replay.Inputs.Where(i => i.Type == KeyType.KeyDown && i.Action == GIActions.MoveForward));
        Assert.DoesNotContain(replay.Inputs, i => i.Action is GIActions.Drop or GIActions.Jump);
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(.35f)]
    public async Task BodyBlockedApproachSidestepsThenResumesTheSameWaypointWithoutLongRetreat(float jitter)
    {
        // Noon 606923 -> 607243: generic retreat moved 15 pixels away from the
        // body-blocked target. Model the collision boundary, not an instant arrival.
        var replay = new PathReplay { EmitReceipts = true };
        var position = new Point2f(104, 100);
        var held = GIActions.MoveForward;
        var moving = false;
        var cleared = false;
        var blockedPulses = 0;
        replay.PositionAt = _ => position;
        replay.OnInput = (action, type) =>
        {
            if (type == KeyType.KeyDown) { held = action; moving = true; }
            if (type == KeyType.KeyUp && action == held) moving = false;
        };
        replay.AfterDelay = ms =>
        {
            if (!moving) return;
            if (held == GIActions.MoveLeft) position.Y += ms * .02f;
            cleared |= position.Y >= 102;
            if (held == GIActions.MoveBackward) position.X += ms * .01f;
            // A collision can move the marker sideways without getting closer to the target.
            if (held == GIActions.MoveForward && !cleared)
                position.Y = 100 + (++blockedPulses % 2 == 0 ? jitter : -jitter);
            if (held == GIActions.MoveForward && cleared)
            { position.X = Math.Max(100, position.X - .6f); position.Y = Math.Max(100, position.Y - .3f); }
        };
        var error = await Record.ExceptionAsync(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.True(error == null, $"blockedPulses={blockedPulses}, recoveryInputs={replay.RecoveryInputs.Count}, error={error}");
        Assert.True(Math.Pow(position.X - 100, 2) + Math.Pow(position.Y - 100, 2) < 4);
        Assert.Single(replay.RecoveryInputs.Where(input => input.Action == GIActions.MoveLeft && input.Type == KeyType.KeyDown));
        Assert.InRange(replay.Inputs.Count(input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown), 1, 24);
        Assert.DoesNotContain(replay.RecoveryInputs, input => input.Action is GIActions.Jump or GIActions.Drop or GIActions.MoveBackward);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task WallOnTheLeftUsesTheOtherSideBeforeResumingTheSameWaypoint()
    {
        // 2026-10-27 645: the left probe stayed within .05px against the wall.
        // The other side is clear; the recovery still must return to the target.
        var replay = new PathReplay { EmitReceipts = true };
        var position = new Point2f(104, 100);
        GIActions? held = null;
        var cleared = false;
        replay.PositionAt = _ => position;
        replay.OnInput = (action, type) =>
        {
            if (type == KeyType.KeyDown) held = action;
            if (type == KeyType.KeyUp && held == action) held = null;
        };
        replay.AfterDelay = ms =>
        {
            if (held == GIActions.MoveRight) position.Y -= ms * .02f;
            cleared |= position.Y <= 98;
            if (held == GIActions.MoveForward && cleared)
            { position.X = Math.Max(100, position.X - .6f); position.Y = Math.Min(100, position.Y + .3f); }
        };
        await replay.Executor.MoveCloseTo(replay.Point("dash"));
        Assert.True(cleared);
        Assert.True(Math.Pow(position.X - 100, 2) + Math.Pow(position.Y - 100, 2) < 4);
        Assert.Contains(replay.RecoveryInputs, input => input.Action == GIActions.MoveRight && input.Type == KeyType.KeyDown);
        Assert.DoesNotContain(replay.RecoveryInputs, input => input.Action is GIActions.Jump or GIActions.MoveBackward);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(0f, false)]
    [InlineData(.35f, false)]
    [InlineData(.35f, true)]
    public async Task BothSidesBlockedStillFailWithinTheOriginalApproachBudget(float jitter, bool driftingAway)
    {
        var pulses = 0;
        var replay = new PathReplay { EmitReceipts = true,
            PositionAt = _ => new Point2f(104 + (driftingAway ? pulses * .1f : 0),
                100 + (pulses % 2 == 0 ? jitter : -jitter)) };
        replay.OnInput = (action, type) =>
        { if (action == GIActions.MoveForward && type == KeyType.KeyDown) pulses++; };
        var error = await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("dash")));
        Assert.Equal("精确接近目标点超时，重试当前路线分段", error.Message);
        Assert.Equal(24, pulses); // One of the original 25 steps runs the sole recovery.
        Assert.DoesNotContain(replay.RecoveryInputs, input => input.Action is GIActions.Jump or GIActions.Drop or GIActions.MoveBackward);
        Assert.Single(replay.RecoveryInputs.Where(input => input.Action == GIActions.MoveLeft && input.Type == KeyType.KeyDown));
        Assert.Single(replay.RecoveryInputs.Where(input => input.Action == GIActions.MoveRight && input.Type == KeyType.KeyDown));
        Assert.True(replay.Clock.GetUtcNow() - replay.Started < TimeSpan.FromSeconds(60));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task RecoveryCannotUseAQueuedPreInputFrameToSendTheNextAction()
    {
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => new Point2f(104, 100) };
        long? inputCompleted = null;
        GIActions? movement = null;
        replay.OnInput = (action, type) =>
        {
            if (UiOperation.Current?.Name != "path-precise-recovery" ||
                action is not (GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight) || type != KeyType.KeyDown) return;
            movement = action;
            replay.Clock.Advance(TimeSpan.FromMilliseconds(5));
            inputCompleted = replay.Clock.GetTimestamp();
        };
        replay.StampTransform = stamp => inputCompleted is { } completed && UiOperation.Current?.Name == "path-precise-recovery"
            ? stamp with { CapturedTimestamp = completed - 1,
                CapturedAt = replay.Clock.GetUtcNow() - replay.Clock.GetElapsedTime(completed - 1) }
            : stamp;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.DoesNotContain(replay.RecoveryInputs, input => input.Action == GIActions.Jump);
        Assert.NotNull(movement);
        Assert.Contains(replay.RecoveryInputs, input => input.Action == movement && input.Type == KeyType.KeyUp);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(MotionStatus.Climb)]
    [InlineData(MotionStatus.Fly)]
    public async Task GroundApproachRechecksMotionAfterRotation(MotionStatus motion)
    {
        var rotated = false;
        var landed = false;
        var replay = new PathReplay { EmitReceipts = true };
        replay.OnRotateUntil = () => rotated = true;
        replay.MotionAt = _ => landed || !rotated ? MotionStatus.Normal : motion;
        replay.PositionAt = _ => landed ? new Point2f(100, 100) : new Point2f(104, 100);
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.MoveForward && type == KeyType.KeyDown)
                Assert.True(landed, "转向后的新帧已攀爬/飞行，不能继续W");
            if (action == GIActions.Drop && type == KeyType.KeyUp) landed = true;
        };
        replay.AfterDelay = ms => { if (motion == MotionStatus.Fly && ms == 100) landed = true; };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(landed);
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.MoveForward && x.Type == KeyType.KeyDown);
        if (motion == MotionStatus.Climb)
            Assert.Single(replay.Inputs.Where(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyDown));
    }

    [Fact]
    public async Task GroundApproachDetachesFromPillarBeforeResumingMovement()
    {
        var detached = false;
        var replay = new PathReplay { EmitReceipts = true };
        replay.MotionAt = _ => detached ? MotionStatus.Normal : MotionStatus.Climb;
        replay.PositionAt = _ => detached ? new Point2f(100, 100) : new Point2f(103, 100);
        replay.OnInput = (action, type) => { if (action == GIActions.Drop && type == KeyType.KeyUp) detached = true; };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(detached);
        Assert.Single(replay.Inputs.Where(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyDown));
        Assert.Contains(300, replay.Delays);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task DetachMustLandBeforeDistanceCanConfirmArrival()
    {
        var detached = false;
        var afterDrop = 0;
        var replay = new PathReplay { EmitReceipts = true };
        replay.MotionAt = _ => !detached ? MotionStatus.Climb : ++afterDrop <= 3 ? MotionStatus.Fly : MotionStatus.Normal;
        replay.PositionAt = _ => detached ? new Point2f(100, 100) : new Point2f(103, 100);
        replay.OnInput = (action, type) => { if (action == GIActions.Drop && type == KeyType.KeyUp) detached = true; };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(afterDrop >= 4);
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.MoveForward && x.Type == KeyType.KeyDown);
    }

    [Fact]
    public async Task GroundApproachCannotPulseForwardWhileStillClimbing()
    {
        var replay = new PathReplay { EmitReceipts = true, MotionAt = _ => MotionStatus.Climb,
            PositionAt = _ => new Point2f(103, 100) };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.Equal(2, replay.Inputs.Count(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyDown));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.MoveForward && x.Type == KeyType.KeyDown);
    }

    [Fact]
    public async Task CancelledDetachStillReleasesDropAndNeverReportsArrival()
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true,
            MotionAt = _ => MotionStatus.Climb, PositionAt = _ => new Point2f(103, 100) };
        replay.AfterDelay = ms => { if (ms == 60) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.Single(replay.Inputs.Where(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyDown));
        Assert.Single(replay.Inputs.Where(x => x.Action == GIActions.Drop && x.Type == KeyType.KeyUp));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.MoveForward && x.Type == KeyType.KeyDown);
    }

    [Fact]
    public async Task NextPositionCaptureWaitsForGameResponseAfterMovementRelease()
    {
        var replay = new PathReplay { EmitReceipts = true };
        DateTimeOffset? releasedAt = null;
        var observedAfterRelease = false;
        replay.PositionAt = _ => releasedAt.HasValue ? new Point2f(100, 100) : new Point2f(104, 100);
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.MoveForward && type == KeyType.KeyUp)
                releasedAt = replay.Clock.GetUtcNow();
        };
        replay.BeforeCapture = _ =>
        {
            if (releasedAt is not { } released) return;
            observedAfterRelease = true;
            Assert.True(replay.Clock.GetUtcNow() - released >= TimeSpan.FromMilliseconds(60),
                "不能在游戏30–60ms响应窗口尚未结束时重新识别位置");
        };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(observedAfterRelease);
        Assert.Single(replay.Inputs.Where(input => input.Type == KeyType.KeyDown));
        Assert.Empty(replay.RecoveryInputs);
    }

    [Fact]
    public async Task KnownTransformationCanConfirmItsDirectPositionWithoutOrdinaryAvatarHud()
    {
        var replay = new PathReplay { Transformed = true, Hud = false };
        await replay.Executor.MoveCloseTo(replay.Point("climb"));
        Assert.Empty(replay.RecoveryInputs);
        Assert.Empty(replay.Inputs.Where(input => input.Type == KeyType.KeyDown));
    }

    [Fact]
    public async Task WaitingForValidFramesDoesNotSpendTheMovementAttemptBudget()
    {
        var replay = new PathReplay { HudAt = count => count > 30 };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.True(replay.Frames > 30);
        Assert.Empty(replay.Inputs.Where(input => input.Type == KeyType.KeyDown));
    }

    [Theory]
    [InlineData("climb", MotionStatus.Climb, false)]
    [InlineData("dash", MotionStatus.Fly, false)]
    [InlineData("climb", MotionStatus.Normal, true)]
    [InlineData("dash", MotionStatus.Unknown, false)]
    public async Task AuthoredGroundRecoveryModesNeverOverrideActualUnsafePosture(string mode, MotionStatus motion, bool transformed)
    {
        var replay = new PathReplay { EmitReceipts = true, Transformed = transformed,
            PositionAt = _ => new Point2f(104, 100), MotionAt = _ => motion };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point(mode)));
        Assert.Empty(replay.RecoveryInputs);
    }

    [Theory]
    [InlineData("climb")]
    [InlineData("dash")]
    public async Task RecordedGroundStallUsesObservedPostureNotAuthoredTransitMode(string mode)
    {
        var replay = new PathReplay { EmitReceipts = true };
        var escaped = false;
        replay.PositionAt = _ => escaped ? new Point2f(39055.75f, 32238.62f) : new Point2f(39058.55f, 32238.57f);
        replay.OnInput = (action, type) =>
        {
            if (type == KeyType.KeyDown && action is GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight)
                escaped = true;
        };
        var target = replay.Point(mode);
        target.X = 39055.75; target.Y = 32238.62;
        await replay.Executor.MoveCloseTo(target);
        Assert.True(escaped);
        Assert.NotEmpty(replay.RecoveryInputs);
    }

    [Theory]
    [InlineData(100, 100, 103.94f, 100)]
    // S56 20260924.log:29801，07-跳崖点东x18.json segment=1 node=79。
    [InlineData(1871.02f, 1256.85f, 1870.55f, 1252.94f)]
    public async Task ConfirmedGroundStallRecoversOnceThenRequiresActualTwoPixelArrival(
        float targetX, float targetY, float stalledX, float stalledY)
    {
        var replay = new PathReplay { EmitReceipts = true };
        var escaped = false;
        replay.PositionAt = _ => escaped ? new Point2f(targetX, targetY) : new Point2f(stalledX, stalledY);
        replay.OnInput = (action, type) =>
        {
            if (type == KeyType.KeyDown && action is GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight)
                escaped = true;
        };
        var target = replay.Point("walk");
        target.X = targetX;
        target.Y = targetY;
        await replay.Executor.MoveCloseTo(target);
        Assert.True(escaped);
        Assert.NotEmpty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyApproachAllowsCumulativeSmallGainsAndShortDetoursWithoutRecovery(bool detours)
    {
        var pulses = 0;
        var distances = detours
            ? new[] { 6f, 6.4f, 6.2f, 6.3f, 5.5f, 5.9f, 5.7f, 5.6f, 5f, 4.8f, 4.5f, 4.7f, 4f, 3.8f, 3.5f, 3.2f, 3.3f, 2.8f, 2.4f, 1.9f }
            : Enumerable.Range(0, 25).Select(step => 2.9f - .04f * step).ToArray();
        var replay = new PathReplay { EmitReceipts = true,
            PositionAt = _ => new Point2f(100 + distances[Math.Min(pulses, distances.Length - 1)], 100) };
        replay.OnInput = (action, type) =>
        { if (action == GIActions.MoveForward && type == KeyType.KeyDown) pulses++; };
        await replay.Executor.MoveCloseTo(replay.Point("walk"));
        Assert.Empty(replay.RecoveryInputs);
        Assert.InRange(pulses, 1, 24);
        Assert.True(distances[pulses] < 2);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData("duplicate")]
    [InlineData("stale")]
    [InlineData("transformed")]
    [InlineData("fly")]
    [InlineData("climb")]
    [InlineData("no-receipt")]
    public async Task IneligibleStallNeverStartsRecoveryOrReportsArrival(string kind)
    {
        var replay = new PathReplay { EmitReceipts = kind != "no-receipt", Direct = kind != "fallback",
            Duplicate = kind == "duplicate", Transformed = kind == "transformed",
            PositionAt = frame => new Point2f(103.94f, 100 + (frame % 4 < 2 ? .35f : -.35f)) };
        if (kind == "stale")
        {
            replay.Clock.Advance(TimeSpan.FromSeconds(3));
            replay.StampTransform = stamp => stamp with { CapturedTimestamp = 0, CapturedAt = replay.Started };
        }
        if (kind == "fly") replay.MotionAt = _ => MotionStatus.Fly;
        if (kind == "climb") replay.MotionAt = _ => MotionStatus.Climb;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.Empty(replay.RecoveryInputs);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationOrParentDeadlineDuringRecoveryCleansKeysAndCannotSucceed(bool cancel)
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true,
            PositionAt = frame => new Point2f(103.94f, 100 + (frame % 4 < 2 ? .35f : -.35f)) };
        replay.OnInput = (_, _) =>
        {
            if (UiOperation.Current?.Name != "path-precise-recovery") return;
            if (cancel) cancellation.Cancel();
            else replay.Clock.Advance(TimeSpan.FromSeconds(11));
        };
        var error = await Record.ExceptionAsync(() => replay.Executor.MoveCloseTo(replay.Point("walk")));
        Assert.NotNull(error);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<RetryException>(error);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task RecoveryWithoutPositionProgressStillFailsRatherThanCreditingTheWaypoint()
    {
        var replay = new PathReplay { EmitReceipts = true, PositionAt = _ => new Point2f(1870.55f, 1252.94f) };
        var target = replay.Point("walk");
        target.X = 1871.02f;
        target.Y = 1256.85f;
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveCloseTo(target));
        Assert.NotEmpty(replay.RecoveryInputs);
        var recoveryMoves = replay.RecoveryInputs.Where(input => input.Type == KeyType.KeyDown &&
            input.Action is GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight).ToArray();
        Assert.Equal(new[] { GIActions.MoveLeft, GIActions.MoveRight }, recoveryMoves.Select(input => input.Action));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }
}
