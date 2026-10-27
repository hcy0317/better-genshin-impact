using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class FlightHandoffTests
{
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
