using BetterGenshinImpact.GameTask.AutoFight;
using Microsoft.Extensions.Time.Testing;
using Fischless.GameCapture;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatBattleHostTests
{
    [Theory]
    [InlineData("repeated")]
    [InlineData("foreign")]
    [InlineData("late")]
    [InlineData("unavailable")]
    public async Task HintCannotBypassObservationGate(string invalid)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        CaptureFrameStamp latest = default;
        io.TargetFactory = stamp =>
        {
            latest = stamp;
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
            { SearchHint = new(stamp, new(558, 775, 30, 24, 411), 1920, 1080) };
        };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 1000 && host.CameraRequests < 2; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(2, host.CameraRequests);
        var old = latest;
        var foreign = new CaptureFrameSource(clock);
        io.TargetFactory = stamp =>
        {
            var source = invalid == "repeated" ? old : invalid == "foreign" ? foreign.Next() : stamp;
            return new(source, flow.Context.BattleId, invalid == "late" ? CombatObservationQuality.Late :
                invalid == "unavailable" ? CombatObservationQuality.Unavailable : CombatObservationQuality.Available,
                null, 1920, 1080)
            { SearchHint = new(source, new(558, 775, 30, 24, 411), 1920, 1080) };
        };
        var count = io.Inputs.Count;
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Equal(count, io.Inputs.Count);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("dimensions")]
    public async Task UnboundHintKeepsTheOriginalBlindScan(string mismatch)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var foreign = new CaptureFrameSource(clock);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId,
            CombatObservationQuality.Available, null, 1920, 1080)
        { SearchHint = new(mismatch == "source" ? foreign.Next() : stamp,
            new(558, 775, 30, 24, 411), mismatch == "dimensions" ? 1280 : 1920, 1080) };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 2000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Contains(io.Inputs, x => x.Kind == CombatBattleHostInputKind.Camera && x.Y != 0);
        Assert.DoesNotContain(io.Traces, x => x.Reason == "scan-unconfirmed-direction-hint");
    }

    [Fact]
    public async Task IndependentTargetAfterHintResumesOriginalStrategy()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId,
            CombatObservationQuality.Available, null, 1920, 1080)
        { SearchHint = new(stamp, new(558, 775, 30, 24, 411), 1920, 1080) };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 1000 && host.CameraRequests < 2; i++)
            await host.AdvanceAsync(flow, default);
        Assert.Equal(2, host.CameraRequests);
        var before = game.Inputs;
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                new(920, 400, 80, 5, 400), 1, SeekCueKind.HealthBar), 1920, 1080)
        { SearchHint = new(stamp, new(558, 775, 30, 24, 411), 1920, 1080) };
        for (var i = 0; i < 100 && game.Inputs == before; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.True(game.Inputs > before);
    }

    [Fact]
    public async Task UnconfirmedHintOnlySteersCameraWithinOriginalSearchBudget()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId,
            CombatObservationQuality.Available, null, 1920, 1080)
        { SearchHint = new(stamp, new(558, 775, 30, 24, 411), 1920, 1080) };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 2000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        var cameras = io.Inputs.Where(x => x.Kind == CombatBattleHostInputKind.Camera).ToArray();
        Assert.Equal(24, cameras.Length);
        Assert.All(cameras, x => { Assert.InRange(x.X, -120, -1); Assert.Equal(0, x.Y); });
        Assert.DoesNotContain(io.Inputs, x => x.Kind == CombatBattleHostInputKind.Approach);
    }
}
