using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatBattleHostTests
{
    [Fact]
    public async Task UnsentRequestExpiresEvenWhenExternalAuthorityHasNoFreshFrame()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { UnsentAttempts = int.MaxValue };
        using var host = new CombatBattleHost(io, new()
        {
            ExternalCompletionAuthority = true, TimeoutSeconds = 0,
            FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1
        });
        for (var i = 0; i < 100 && io.Requests.Count == 0; i++)
            await host.AdvanceAsync(flow, default);
        var request = Assert.Single(io.Requests);
        io.TargetFactory = _ => new(request.Source, flow.Context.BattleId,
            CombatObservationQuality.Available, null, 1920, 1080);
        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(CombatBattleHostResult.Unconfirmed, await host.AdvanceAsync(flow, default));
        Assert.Equal("host-input-request-deadline", host.Reason);
        Assert.Single(io.Requests);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task UnsentCameraCannotDonateItsRequestToApproach()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { UnsentAttempts = 1 };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 100 && io.Requests.Count == 0; i++)
            await host.AdvanceAsync(flow, default);
        Assert.Single(io.Requests);
        Assert.Equal(CombatBattleHostInputKind.Camera, io.Requests[0].Kind);
        Assert.Empty(io.Inputs);

        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(920, 400, 80, 4, 320), 1, SeekCueKind.HealthBar), 1920, 1080)
        { Control = new(MotionStatus.Unknown, false) };
        for (var i = 0; i < 100 && io.Requests.Count < 2; i++)
            await host.AdvanceAsync(flow, default);

        Assert.Equal(2, io.Requests.Count);
        Assert.Equal(CombatBattleHostInputKind.Approach, io.Requests[1].Kind);
        Assert.NotEqual(io.Requests[0].RequestId, io.Requests[1].RequestId);
        Assert.True(io.Requests[1].DeadlineTimestamp > io.Requests[0].DeadlineTimestamp);
    }
}
