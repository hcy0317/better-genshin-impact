using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class RecordedFlyTransitTests
{
    // 301:13 dash/path→14 fly/path(empty action)→15 walk/path；晶蝶8 fly/target(wait3.5)→9 fly/path(empty)→10 walk/target。
    [Theory]
    [InlineData(14, -3928.8437, -3264.8444, false)]
    [InlineData(14, -3928.8437, -3264.8444, true)]
    [InlineData(9, -3167.5469, -3693.8086, false)]
    [InlineData(9, -3167.5469, -3693.8086, true)]
    public async Task RecordedEmptyFlyTransitCanArriveOnGroundOrInAir(int id, double x, double y, bool airborne)
    {
        var replay = new PathReplay();
        replay.PositionAt = _ => new((float)x, (float)y);
        replay.MotionAt = _ => airborne ? MotionStatus.Fly : MotionStatus.Normal;
        var point = new WaypointForTrack(new Waypoint { Id=id, X=x, Y=y, Type="path", MoveMode="fly", Action="" },
            new RouteMapContext("Teyvat", "SIFT", null), p => p);
        await replay.Executor.MoveTo(point);
        Assert.True(replay.Clock.GetUtcNow() - replay.Started < TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.Jump);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransitStillRequiresDirectFreshPosition(bool fallback)
    {
        var replay = new PathReplay { Direct = !fallback, Duplicate = !fallback };
        var point = replay.Point("fly");
        point.Type = "path"; point.Action = "";
        if (!fallback) replay.StampTransform = stamp => stamp with { CapturedTimestamp = stamp.CapturedTimestamp - 3 * stamp.TimestampFrequency };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(point));
        Assert.DoesNotContain(replay.Inputs, x => x.Action == GIActions.Jump);
        Assert.Equal(KeyType.KeyUp, replay.Inputs[^1].Type);
    }
}
