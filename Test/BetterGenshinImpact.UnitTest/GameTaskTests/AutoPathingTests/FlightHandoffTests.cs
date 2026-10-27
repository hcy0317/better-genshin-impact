using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class FlightHandoffTests
{
    [Fact]
    public async Task AttachedFlyStartDetachesOnFreshFeedbackBeforeOpeningWings()
    {
        var replay = new PathReplay { EmitReceipts = true };
        var detached = false;
        var airborne = false;
        var forward = false;
        DateTimeOffset? dropDown = null;
        DateTimeOffset? releasedAt = null;
        replay.PositionAt = _ => airborne ? new Point2f(100, 100) : new Point2f(120, 100);
        replay.MotionAt = _ => airborne ? MotionStatus.Fly : detached ? MotionStatus.Normal : MotionStatus.Climb;
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.MoveForward) forward = type == KeyType.KeyDown;
            if (action == GIActions.Drop && type == KeyType.KeyDown) dropDown = replay.Clock.GetUtcNow();
            if (action == GIActions.Drop && type == KeyType.KeyUp)
            {
                Assert.NotNull(dropDown);
                Assert.True(replay.Clock.GetUtcNow() - dropDown.Value >= TimeSpan.FromMilliseconds(60));
                detached = true;
                releasedAt = replay.Clock.GetUtcNow();
            }
            if (action == GIActions.Jump && type == KeyType.KeyDown)
            {
                Assert.True(detached, "An attached avatar cannot open wings by jumping on the wall.");
                Assert.True(replay.Clock.GetUtcNow() - releasedAt!.Value >= TimeSpan.FromMilliseconds(60));
            }
            if (action == GIActions.Jump && type == KeyType.KeyUp && detached && forward) airborne = true;
        };
        replay.OnRotateUntil = () => Assert.True(airborne);
        await replay.Executor.MoveTo(replay.Point("fly"));
        Assert.True(detached);
        Assert.True(airborne);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task AttachedFlyStartDoesNotRepeatADetachThatDidNotChangePose()
    {
        var replay = new PathReplay { EmitReceipts = true, MotionAt = _ => MotionStatus.Climb,
            PositionAt = _ => new Point2f(120, 100) };
        await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(
            () => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.Single(replay.Inputs.Where(input => input.Action == GIActions.Drop && input.Type == KeyType.KeyDown));
        Assert.Single(replay.Inputs.Where(input => input.Action == GIActions.Drop && input.Type == KeyType.KeyUp));
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Jump);
        Assert.InRange((replay.Clock.GetUtcNow() - replay.Started).TotalSeconds, 240, 240.3);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AttachedFlyStartRequiresFreshDirectPositionBeforeDetach(bool duplicate)
    {
        var replay = new PathReplay { EmitReceipts = true, Direct = duplicate, Duplicate = duplicate,
            MotionAt = _ => MotionStatus.Climb, PositionAt = _ => new Point2f(120, 100) };
        await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(
            () => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Drop || input.Action == GIActions.Jump);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task AttachedFlyStartRejectsAnUnconfirmedDetachReceipt()
    {
        var replay = new PathReplay { MotionAt = _ => MotionStatus.Climb,
            PositionAt = _ => new Point2f(120, 100) };
        var failure = await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(
            () => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.Contains("脱离输入未确认", failure.Message);
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Jump);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task AttachedFlyStartReleasesDropWhenCancelledDuringItsHold()
    {
        using var cancel = new CancellationTokenSource();
        var replay = new PathReplay(cancel.Token) { EmitReceipts = true,
            MotionAt = _ => MotionStatus.Climb, PositionAt = _ => new Point2f(120, 100) };
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.Drop && type == KeyType.KeyDown) cancel.Cancel();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.Single(replay.Inputs.Where(input => input.Action == GIActions.Drop && input.Type == KeyType.KeyUp));
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Jump);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task AttachedFlyStartCannotUsePreDetachPixelsAsArrivalFeedback()
    {
        var detached = false;
        long beforeDetach = 0;
        var replay = new PathReplay { EmitReceipts = true };
        replay.PositionAt = _ => detached ? new Point2f(100, 100) : new Point2f(120, 100);
        replay.MotionAt = _ => detached ? MotionStatus.Fly : MotionStatus.Climb;
        replay.StampTransform = stamp => detached ? stamp with { CapturedTimestamp = beforeDetach } : stamp;
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.Drop && type == KeyType.KeyDown) beforeDetach = replay.Clock.GetTimestamp();
            if (action == GIActions.Drop && type == KeyType.KeyUp) detached = true;
        };
        replay.OnRotateUntil = () => throw new InvalidOperationException("Pre-detach pixels cannot authorize the handoff.");
        await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(
            () => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.True(detached);
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Jump);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task GroundedFlyStartAdvancesTowardTheEdgeInsteadOfJumpingInPlace()
    {
        var replay = new PathReplay { EmitReceipts = true };
        var advancing = false;
        var airborne = false;
        replay.PositionAt = _ => airborne ? new Point2f(100, 100) : new Point2f(120, 100);
        replay.MotionAt = _ => airborne ? MotionStatus.Fly : MotionStatus.Normal;
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.MoveForward) advancing = type == KeyType.KeyDown;
            if (action == GIActions.Jump && type == KeyType.KeyUp && advancing) airborne = true;
        };
        replay.OnRotateUntil = () => Assert.True(airborne, "Do not block a falling start on initial heading.");
        await replay.Executor.MoveTo(replay.Point("fly"));
        Assert.True(airborne);
        Assert.Contains(replay.Inputs, input => input.Action == GIActions.MoveForward && input.Type == KeyType.KeyDown);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task FallingFlyStartOpensWingsBeforeBlockingInitialRotation()
    {
        var replay = new PathReplay { EmitReceipts = true };
        var flying = false;
        var rotated = false;
        replay.MotionAt = _ => flying ? MotionStatus.Fly : MotionStatus.Normal;
        replay.PositionAt = _ => rotated ? new Point2f(100, 100) : new Point2f(120, 100);
        replay.OnInput = (action, type) => { if (action == GIActions.Jump && type == KeyType.KeyUp) flying = true; };
        replay.OnRotateUntil = () =>
        {
            Assert.True(flying, "Falling traversal must open wings before a multi-second heading operation.");
            var released = replay.Inputs.Last(input => input.Action == GIActions.Jump && input.Type == KeyType.KeyUp);
            Assert.True(replay.Clock.GetUtcNow() - released.At >= TimeSpan.FromMilliseconds(200));
            rotated = true;
        };
        await replay.Executor.MoveTo(replay.Point("fly"));
        Assert.True(rotated);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task AlreadyFlyingWalkHandoffDoesNotSwitchOrBlockOnInitialHeading()
    {
        var replay = new PathReplay { EmitReceipts = true, MotionAt = _ => MotionStatus.Fly };
        replay.PositionAt = frame => frame < 5 ? new Point2f(120, 100) : new Point2f(100, 100);
        replay.OnRotateUntil = () => throw new InvalidOperationException("Do not pause airborne traversal for initial heading.");
        await replay.Executor.MoveTo(replay.Point("walk"));
        Assert.Equal(0, replay.SwitchCalls);
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Jump && input.Type == KeyType.KeyDown);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }

    [Fact]
    public async Task SamePositionPlainFlyTransitNeedsNoJumpOrInitialHeading()
    {
        var replay = new PathReplay { EmitReceipts = true };
        replay.OnRotateUntil = () => throw new InvalidOperationException("An already reached plain transit has no heading work.");
        var point = replay.Point("fly");
        point.Type = WaypointType.Path.Code;
        point.Action = null;
        await replay.Executor.MoveTo(point);
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Jump && input.Type == KeyType.KeyDown);
    }

    [Fact]
    public async Task FallbackLocationCannotStartWingInputOrInitialFlightHeading()
    {
        var replay = new PathReplay { EmitReceipts = true, Direct = false,
            PositionAt = _ => new Point2f(120, 100) };
        var rotated = false;
        replay.OnRotateUntil = () => rotated = true;
        await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.False(rotated);
        Assert.DoesNotContain(replay.Inputs, input => input.Action == GIActions.Jump && input.Type == KeyType.KeyDown);
    }
}
