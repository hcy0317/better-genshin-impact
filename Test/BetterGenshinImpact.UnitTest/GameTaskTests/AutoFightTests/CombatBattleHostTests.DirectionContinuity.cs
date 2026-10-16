using BetterGenshinImpact.GameTask.AutoFight;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatBattleHostTests
{
    [Theory]
    [InlineData("Left", -90)]
    [InlineData("Right", 90)]
    [InlineData("Behind", 179)]
    public async Task ExhaustedOffscreenDirectionCannotResumeBlindAttacks(string directionName, double bearing)
    {
        // 2026-10-16: 24/24 camera pulses followed by a persistent offscreen arrow
        // resumed the water strategy and eventually failed as busy-without-game-progress.
        var direction = Enum.Parse<EnemyIndicatorDirection>(directionName);
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.Approach, direction, new(447, 408, 33, 30, 392, bearing), 1,
                SeekCueKind.DirectionIndicator), 1920, 1080);
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(24, host.CameraRequests);
        Assert.Equal(0, game.Inputs);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Equal("offscreen-target-search-exhausted", host.Reason);
    }

    [Fact]
    public async Task ForwardHintAtASafeBoundaryCannotStarveTheStrategy()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.Approach, EnemyIndicatorDirection.Forward,
                new(945, 100, 25, 25, 300, 0), 1, SeekCueKind.DirectionIndicator), 1920, 1080);
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false });
        for (var i = 0; i < 20; i++) await host.AdvanceAsync(flow, default);
        Assert.True(game.Inputs > 0);
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SearchEpisodesDoNotInheritThePreviousReasonOrBehindTurnSide(bool ordinaryFinishSearch)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("strategy(loop=battle)\n琴 attack(.1,required)\n琴 e"), game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var direction = EnemyIndicatorDirection.Behind;
        var bearing = 179d;
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.Approach, direction, new(945, 800, 25, 25, 300, bearing), 1,
                SeekCueKind.DirectionIndicator), 1920, 1080);
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 100 && io.Inputs.Count == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.True(Assert.Single(io.Inputs).X > 0);
        direction = EnemyIndicatorDirection.Forward;
        bearing = 0;
        for (var i = 0; i < 100 && host.State != "Fighting"; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal("Fighting", host.State);
        if (ordinaryFinishSearch)
        {
            await host.AdvanceAsync(flow, default);
            Assert.Equal(1, game.Inputs);
            io.TargetFactory = null;
            clock.Advance(TimeSpan.FromSeconds(46));
        }
        else { direction = EnemyIndicatorDirection.Behind; bearing = -179; }
        var previousActions = game.Inputs;
        for (var i = 0; i < 100 && io.Inputs.Count < 2; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(2, io.Inputs.Count);
        if (ordinaryFinishSearch) Assert.Equal(previousActions, game.Inputs);
        else Assert.True(io.Inputs[1].X < 0);
    }

    [Fact]
    public void DirectionBearingSelectsASmallStepNotAnEstimatedYawDistance()
    {
        var a = new EnemySeekVisual(1350, 450, 25, 25, 300, 25);
        var b = a with { IndicatorBearingDegrees = 80 };
        var first = AutoFightSeek.GetIndicatorCameraOffset(EnemyIndicatorDirection.Right, a, 1920, 1080);
        Assert.InRange(first, 1, 120);
        Assert.Equal(first, AutoFightSeek.GetIndicatorCameraOffset(EnemyIndicatorDirection.Right, b, 1920, 1080));
    }

    [Fact]
    public async Task BehindHintKeepsItsTurnSideAcrossTheSignedAngleBoundary()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var bearing = 179d;
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.Approach, EnemyIndicatorDirection.Behind,
                new(945, 800, 25, 25, 300, bearing), 1, SeekCueKind.DirectionIndicator), 1920, 1080);
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false });
        await host.AdvanceAsync(flow, default);
        clock.Advance(TimeSpan.FromSeconds(46));
        for (var i = 0; i < 100 && io.Inputs.Count < 1; i++) await host.AdvanceAsync(flow, default);
        Assert.Single(io.Inputs);
        bearing = -179;
        for (var i = 0; i < 100 && io.Inputs.Count < 2; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(2, io.Inputs.Count);
        Assert.Equal(Math.Sign(io.Inputs[0].X), Math.Sign(io.Inputs[1].X));
        io.TargetFactory = null;
        var at = flow.Context.Now;
        while (flow.Context.Now < at + .6) await host.AdvanceAsync(flow, default);
        Assert.Equal(2, io.Inputs.Count);
        for (var i = 0; i < 100 && io.Inputs.Count < 3; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(3, io.Inputs.Count); // Missing hint is bounded, not a success or an endless pause.
    }
}
