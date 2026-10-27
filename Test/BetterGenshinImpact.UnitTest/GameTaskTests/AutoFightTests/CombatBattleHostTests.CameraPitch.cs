using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatBattleHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CameraResetWaitsForFiveHundredMillisecondsAndFreshWorldWithinOriginalDeadline(bool foreignFeedback)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
            { Control = new(MotionStatus.Unknown, false) };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 300 && !io.Inputs.Any(input => input.Kind == CombatBattleHostInputKind.CameraReset); i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        var reset = Assert.Single(io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.CameraReset));
        var completed = clock.GetTimestamp();
        var inputCount = io.Inputs.Count;

        // An advancing sequence is insufficient: pixels taken at 490ms must not release the barrier,
        // even while that frame is fresh when delivered after the 500ms settle interval.
        clock.Advance(TimeSpan.FromMilliseconds(510));
        io.TargetFactory = stamp => new(stamp with
            { CapturedTimestamp = completed + clock.TimestampFrequency * 490 / 1000 },
            flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
            { Control = new(MotionStatus.Unknown, false) };
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("host-camera-reset-awaiting-fresh-evidence", host.Reason);
        Assert.Equal(inputCount, io.Inputs.Count);
        Assert.All(io.Traces, trace => Assert.Null(trace.LastProgress));

        io.TargetFactory = stamp => new(foreignFeedback ? stamp with { SessionId = Guid.NewGuid() } : stamp,
            flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
            { Control = new(MotionStatus.Unknown, false) };
        if (!foreignFeedback)
        {
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
            Assert.Equal("host-camera-reset-rechecked", host.Reason);
            Assert.Equal(inputCount, io.Inputs.Count);
            Assert.True(clock.GetElapsedTime(completed).TotalMilliseconds >= 500);
        }
        else
        {
            var result = CombatBattleHostResult.Continue;
            for (var i = 0; i < 400 && result == CombatBattleHostResult.Continue; i++)
                result = await host.AdvanceAsync(flow, default);
            Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
            Assert.True(clock.GetTimestamp() >= reset.DeadlineTimestamp);
            Assert.True(clock.GetTimestamp() < reset.DeadlineTimestamp + clock.TimestampFrequency / 10);
            Assert.Equal(inputCount, io.Inputs.Count);
        }
        Assert.Equal(1, io.Traces.Max(trace => trace.ScanUsed));
        Assert.All(io.Traces, trace => Assert.Null(trace.LastProgress));
        Assert.InRange(io.Traces[^1].ProgressAge, clock.GetElapsedTime(started).TotalSeconds - .1,
            clock.GetElapsedTime(started).TotalSeconds);
        Assert.DoesNotContain(io.Traces, trace => trace.Result == CombatBattleHostResult.Completed);
    }
}
