using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatBattleHostTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistentAmbiguityOrChangingCandidatesCannotRenewSearchForever(bool changing)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var frames = 0;
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.Right,
                new(changing && ++frames % 2 == 0 ? 1450 : 1350, 400, 80, 4, 320),
                changing ? 1 : 2, SeekCueKind.HealthBar), 1920, 1080);
        using var host = new CombatBattleHost(io, new() { TimeoutSeconds = 0,
            FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        await host.AdvanceAsync(flow, default);
        clock.Advance(TimeSpan.FromSeconds(46));
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 2000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Empty(io.Inputs);
        Assert.True(io.ReleaseCount > 0);
        Assert.DoesNotContain(io.Traces, trace => trace.Result == CombatBattleHostResult.Completed);
        if (!changing) Assert.True(game.Inputs > 0, "Ambiguous movement should fall back to the existing bounded strategy.");
    }
}
