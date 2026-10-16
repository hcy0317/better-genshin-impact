using BetterGenshinImpact.GameTask.AutoFight;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatBattleHostTests
{
    [Theory]
    [InlineData("epoch")]
    [InlineData("direction")]
    [InlineData("ambiguous")]
    [InlineData("target")]
    public async Task SimilarRectanglesCannotRetainAnInvalidatedBusinessIntent(string change)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var changed = false;
        var attempts = new List<CombatBattleHostInput>();
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy,
                changed && change == "direction" ? EnemyIndicatorDirection.Left : EnemyIndicatorDirection.Right,
                new(changed && change == "target" ? 1450 : 1350, 400, 80, 4, 320),
                changed && change == "ambiguous" ? 2 : 1, SeekCueKind.HealthBar), 1920, 1080)
            { CaptureEpoch = changed && change == "epoch" ? 2 : 1 };
        io.PrepareEvidence = input => { attempts.Add(input); return true; };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        await host.AdvanceAsync(flow, default);
        clock.Advance(TimeSpan.FromSeconds(46));
        for (var i = 0; i < 200 && attempts.Count == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.Single(attempts);
        changed = true;
        await host.AdvanceAsync(flow, default);
        Assert.Empty(io.Inputs);
        Assert.Single(attempts);
        Assert.Equal("host-input-candidate-changed", host.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task SmallSingleTargetJitterKeepsTheOriginalInputRequestAndDeadline(int jitter)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var attempts = new List<CombatBattleHostInput>();
        var offset = 0;
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.Right,
                new(1350 + offset, 400, 80, 4, 320), 1, SeekCueKind.HealthBar), 1920, 1080);
        io.PrepareEvidence = input => { attempts.Add(input); return attempts.Count > 1; };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        await host.AdvanceAsync(flow, default);
        clock.Advance(TimeSpan.FromSeconds(46));
        for (var i = 0; i < 200 && attempts.Count == 0; i++) await host.AdvanceAsync(flow, default);
        var original = Assert.Single(attempts);
        offset = jitter;
        for (var i = 0; i < 200 && io.Inputs.Count == 0; i++) await host.AdvanceAsync(flow, default);
        var sent = Assert.Single(io.Inputs);
        Assert.Equal(original.RequestId, sent.RequestId);
        Assert.Equal(original.DeadlineTimestamp, sent.DeadlineTimestamp);
        Assert.True(sent.Source.IsAfter(original.Source));
    }

    [Fact]
    public async Task DiagnosticReadinessOrFailureCannotChangeBusinessInputsOrTermination()
    {
        async Task<(string Inputs, CombatBattleHostResult Result, string Reason)> Run(int diagnostics)
        {
            var clock = new FakeTimeProvider();
            using var flow = CreateFlow(false, new ReturningGame(clock), clock);
            var io = new ReplayIo(clock, flow.Context.BattleId)
            { PrepareEvidence = _ => diagnostics == 2 ? throw new IOException("diagnostic-slot-failure") : diagnostics == 0 };
            using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false,
                FinishCheckIntervalSeconds = .1, TimeoutSeconds = 4 });
            var result = CombatBattleHostResult.Continue;
            for (var i = 0; i < 300 && result == CombatBattleHostResult.Continue; i++) result = await host.AdvanceAsync(flow, default);
            var inputs = string.Join(";", io.Inputs.Select(input => $"{input.Kind}:{input.X}:{input.Y}:{input.Source.Sequence}:{input.Source.CapturedTimestamp}"));
            Assert.NotEmpty(io.Inputs);
            return (inputs, result, host.Reason);
        }
        var normal = await Run(0);
        Assert.Equal(normal, await Run(1));
        Assert.Equal(normal, await Run(2));
    }
}
