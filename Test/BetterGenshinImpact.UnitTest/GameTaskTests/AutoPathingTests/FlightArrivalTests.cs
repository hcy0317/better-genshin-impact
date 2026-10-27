using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class FlightArrivalTests
{
    [Fact]
    public async Task ConfirmedFlightAtNode22ContinuesWithoutTheGroundSettle()
    {
        // 2026-10-27 05:00:11: fly/target, empty action, distance 1.75.
        // A one-second ground settle loses altitude before the next node.
        var replay = new PathReplay { MotionAt = _ => MotionStatus.Fly };
        replay.AfterDelay = ms => Assert.True(ms < 1000, "飞行到达不能原地等待地面稳定时间");
        var point = replay.Point("fly");
        point.Id = 22; point.Action = null;
        await replay.Executor.MoveTo(point);
        await replay.Executor.MoveCloseTo(point);
        Assert.Equal(new[] { 60 }, replay.Delays);
        Assert.DoesNotContain(replay.Inputs, input => input.Type != KeyType.KeyUp && input.Action != GIActions.MoveForward);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("walk", MotionStatus.Normal)]
    [InlineData("run", MotionStatus.Normal)]
    [InlineData("dash", MotionStatus.Normal)]
    [InlineData("fly", MotionStatus.Normal)]
    [InlineData("fly", MotionStatus.Unknown)]
    public async Task GroundArrivalKeepsItsOneSecondSettle(string mode, MotionStatus motion)
    {
        var replay = new PathReplay { MotionAt = _ => motion };
        var point = replay.Point(mode); point.Action = null;
        await replay.Executor.MoveCloseTo(point);
        Assert.Equal(new[] { 1000 }, replay.Delays);
    }

    [Fact]
    public async Task PreviousFlightCannotSkipSettlingAfterLanding()
    {
        var replay = new PathReplay { MotionAt = _ => MotionStatus.Fly };
        var point = replay.Point("fly"); point.Action = null;
        await replay.Executor.MoveTo(point);
        replay.MotionAt = _ => MotionStatus.Normal;
        await replay.Executor.MoveCloseTo(point);
        Assert.Equal(new[] { 1000 }, replay.Delays);
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("fallback")]
    [InlineData("hud")]
    public async Task UnconfirmedFlightCannotCompletePreciseArrival(string missing)
    {
        var replay = new PathReplay { MotionAt = _ => MotionStatus.Fly };
        var point = replay.Point("fly"); point.Action = null;
        await replay.Executor.MoveTo(point);
        replay.Direct = missing != "fallback";
        replay.Hud = missing != "hud";
        if (missing == "stale") replay.StampTransform = stamp => stamp with
            { CapturedTimestamp = stamp.CapturedTimestamp - 3 * stamp.TimestampFrequency };
        await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(
            () => replay.Executor.MoveCloseTo(point));
        Assert.DoesNotContain(60, replay.Delays);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task SourceExpiringDuringArrivalRecognitionCannotBypassGroundSettle()
    {
        var replay = new PathReplay { MotionAt = _ => MotionStatus.Fly };
        replay.ReadTransformation = _ => { replay.Clock.Advance(TimeSpan.FromSeconds(3)); return false; };
        var point = replay.Point("fly"); point.Action = null;
        await replay.Executor.MoveCloseTo(point);
        Assert.Equal(new[] { 1000 }, replay.Delays);
    }

    [Fact]
    public async Task DuplicateFlightFrameAfterApproachPulseCannotConfirmArrival()
    {
        var pulsed = false;
        var replay = new PathReplay { EmitReceipts = true, MotionAt = _ => MotionStatus.Fly };
        replay.PositionAt = _ => pulsed ? new(100, 100) : new(104, 100);
        replay.OnInput = (action, type) =>
        {
            if (action == GIActions.MoveForward && type == KeyType.KeyUp && replay.Inputs.Any(x => x.Type == KeyType.KeyDown))
            { pulsed = true; replay.Duplicate = true; }
        };
        var point = replay.Point("fly"); point.Action = null;
        await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(
            () => replay.Executor.MoveCloseTo(point));
        Assert.True(pulsed);
        Assert.DoesNotContain(1000, replay.Delays);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }

    [Fact]
    public async Task CancellingFlightResponseStillReleasesMovement()
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new PathReplay(cancellation.Token) { MotionAt = _ => MotionStatus.Fly };
        replay.AfterDelay = _ => cancellation.Cancel();
        var point = replay.Point("fly"); point.Action = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.Executor.MoveCloseTo(point));
        Assert.Equal(new[] { 60 }, replay.Delays);
        Assert.Equal((GIActions.MoveForward, KeyType.KeyUp), (replay.Inputs[^1].Action, replay.Inputs[^1].Type));
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }
}
