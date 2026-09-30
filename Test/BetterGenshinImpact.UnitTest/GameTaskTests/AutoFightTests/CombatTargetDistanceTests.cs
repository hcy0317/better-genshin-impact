using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatTargetDistanceTests
{
    [Theory]
    [InlineData(1920, 1080, 900, 100, 30, 24, 360)]
    [InlineData(1280, 720, 600, 67, 20, 16, 160)]
    public void SameIndicatorFootprintIsAcceptedAtItsCaptureScale(int width, int height, int x, int y, int w, int h, int area)
    {
        Assert.True(AutoFightSeek.IsDirectionIndicatorGeometry(new(x, y, w, h, area, 0), width, height));
    }

    [Fact]
    public void FixedBossBarDoesNotHideFloatingTargetPosition()
    {
        var decision = AutoFightSeek.SelectSeekDecision(
            [new(757, 82, 406, 9, 3654), new(910, 400, 100, 4, 400)], 1920, 1080);
        Assert.Equal(SeekCueKind.HealthBar, decision.Cue);
        Assert.Equal(400, decision.Visual!.Value.Y);
        Assert.Equal(82, decision.FixedTopHealth!.Value.Y);
    }

    [Fact]
    public void BossExistenceSurvivesAnUnconfirmedSpatialTarget()
    {
        var clock = new FakeTimeProvider();
        var fixedBar = new EnemySeekVisual(757, 82, 406, 9, 3654);
        var passive = new PassiveTargetObservation(false, false, clock.GetUtcNow().UtcDateTime, null, 1920, 1080)
        { Source = new CaptureFrameSource(clock).Next(), BattleId = Guid.NewGuid(), FixedTopHealth = fixedBar };
        var result = NativeCombatBattleHostIo.ProjectObservation(passive, 0, true, clock.GetUtcNow().UtcDateTime);
        Assert.Equal(SeekCueKind.FixedTopHealth, result.Target?.Cue);
        Assert.Equal(fixedBar, result.FixedTopHealth);
        Assert.False(CombatBattleHost.CanApproach(result, passive.BattleId, clock));
    }

    [Theory]
    [InlineData(1920, 1080, 5, true)]
    [InlineData(1920, 1080, 6, false)]
    [InlineData(1280, 720, 3, true)]
    [InlineData(1280, 720, 4, false)]
    public void PassiveHealthDistanceSurvivesUntilHostMovementAdmission(int width, int height, int barHeight, bool approach)
    {
        var clock = new FakeTimeProvider();
        var battle = Guid.NewGuid();
        var stamp = new CaptureFrameSource(clock).Next();
        var visual = new EnemySeekVisual(width / 2 - 40, height / 3, 80, barHeight, 80 * barHeight);
        var passive = new PassiveTargetObservation(true, false, clock.GetUtcNow().UtcDateTime, visual, width, height);

        Assert.True(AutoFightSeek.TryCreatePassiveDecision(passive, clock.GetUtcNow().UtcDateTime,
            out var decision, out _, out _));
        Assert.Equal(approach ? AutoFightSeekAction.ApproachVisibleEnemy : AutoFightSeekAction.KeepFighting, decision.Action);
        var observation = new CombatBattleObservation(stamp, battle, CombatObservationQuality.Available, decision, width, height)
        { Control = new(MotionStatus.Unknown, false) };
        Assert.Equal(approach, CombatBattleHost.CanApproach(observation, battle, clock));
    }

    [Fact]
    public void KeepFightingCannotBeReinterpretedAsApproach()
    {
        var clock = new FakeTimeProvider();
        var battle = Guid.NewGuid();
        var observation = new CombatBattleObservation(new CaptureFrameSource(clock).Next(), battle,
            CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(920, 400, 80, 6, 480), 1, SeekCueKind.HealthBar),
            1920, 1080) { Control = new(MotionStatus.Unknown, false) };
        Assert.False(CombatBattleHost.CanApproach(observation, battle, clock));
    }
}
