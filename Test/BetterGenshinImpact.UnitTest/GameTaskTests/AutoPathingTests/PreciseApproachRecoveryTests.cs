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
    [InlineData("fallback")]
    [InlineData("duplicate")]
    [InlineData("transformed")]
    [InlineData("fly")]
    [InlineData("climb")]
    [InlineData("no-receipt")]
    public async Task IneligibleStallNeverStartsRecoveryOrReportsArrival(string kind)
    {
        var replay = new PathReplay { EmitReceipts = kind != "no-receipt", Direct = kind != "fallback",
            Duplicate = kind == "duplicate", Transformed = kind == "transformed",
            PositionAt = _ => new Point2f(103.94f, 100) };
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
        var replay = new PathReplay(cancellation.Token) { EmitReceipts = true, PositionAt = _ => new Point2f(103.94f, 100) };
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
        Assert.Single(replay.RecoveryInputs.Where(input => input.Type == KeyType.KeyDown &&
            input.Action is GIActions.MoveBackward or GIActions.MoveLeft or GIActions.MoveRight));
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }
}
