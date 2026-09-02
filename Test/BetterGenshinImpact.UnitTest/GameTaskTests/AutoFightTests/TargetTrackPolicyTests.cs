using System;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class TargetTrackPolicyTests
{
    [Fact]
    public void RejectedChallengerDoesNotReturnAnExecutableDecision()
    {
        AutoFightSeek.ResetSeekState();
        var now = DateTime.UtcNow;
        var current = HealthDecision(new EnemySeekVisual(300, 320, 100, 6, 600));
        var challenger = HealthDecision(new EnemySeekVisual(1500, 320, 100, 6, 600));

        var accepted = AutoFightSeek.UpdateTargetTrack(current, 1920, 1080, now);
        var rejected = AutoFightSeek.UpdateTargetTrack(
            challenger,
            1920,
            1080,
            now.AddMilliseconds(100));

        Assert.Equal(current, accepted.AcceptedDecision);
        Assert.Null(rejected.AcceptedDecision);
        Assert.False(rejected.TargetProgress);
    }

    [Fact]
    public void OnlyFreshPassiveObservationsCanFeedTheBoundedSeekDecision()
    {
        var capturedAt = DateTime.UtcNow;
        var visual = new EnemySeekVisual(850, 320, 100, 6, 600);
        var observation = new PassiveTargetObservation(
            HasNormalHealthBar: true,
            HasDamageCue: false,
            CapturedAtUtc: capturedAt,
            Visual: visual,
            ImageWidth: 1920,
            ImageHeight: 1080);

        Assert.True(AutoFightSeek.TryCreatePassiveDecision(
            observation,
            capturedAt.AddMilliseconds(150),
            out var decision,
            out var imageWidth,
            out var imageHeight));
        Assert.Equal(AutoFightSeekAction.ApproachVisibleEnemy, decision.Action);
        Assert.Equal(SeekCueKind.HealthBar, decision.Cue);
        Assert.Equal(visual, decision.Visual);
        Assert.Equal(1920, imageWidth);
        Assert.Equal(1080, imageHeight);

        Assert.False(AutoFightSeek.TryCreatePassiveDecision(
            observation,
            capturedAt.AddMilliseconds(300),
            out _,
            out _,
            out _));

        var damageObservation = observation with
        {
            HasNormalHealthBar = false,
            HasDamageCue = true
        };
        Assert.True(AutoFightSeek.TryCreatePassiveDecision(
            damageObservation,
            capturedAt.AddMilliseconds(150),
            out var damageDecision,
            out _,
            out _));
        Assert.Equal(SeekCueKind.DamageNumber, damageDecision.Cue);
    }

    [Fact]
    public void DifferentChallengersDoNotShareWinningFrames()
    {
        AutoFightSeek.ResetSeekState();
        var now = DateTime.UtcNow;
        var current = HealthDecision(new EnemySeekVisual(300, 320, 100, 6, 600));
        var challengerA = HealthDecision(new EnemySeekVisual(1500, 320, 100, 6, 600));
        var challengerB = HealthDecision(new EnemySeekVisual(900, 700, 100, 6, 600));

        _ = AutoFightSeek.UpdateTargetTrack(current, 1920, 1080, now);
        Assert.Null(Accept(challengerA, now.AddMilliseconds(100)).AcceptedDecision);
        Assert.Null(Accept(challengerB, now.AddMilliseconds(200)).AcceptedDecision);
        Assert.Null(Accept(challengerA, now.AddMilliseconds(300)).AcceptedDecision);
        Assert.Null(Accept(challengerA, now.AddMilliseconds(400)).AcceptedDecision);
        Assert.Equal(
            challengerA,
            Accept(challengerA, now.AddMilliseconds(500)).AcceptedDecision);

        TargetTrackUpdate Accept(EnemySeekDecision decision, DateTime observedAtUtc)
        {
            return AutoFightSeek.UpdateTargetTrack(decision, 1920, 1080, observedAtUtc);
        }
    }

    [Fact]
    public void ChallengerNeedsThreeWinningFramesWithoutAScoreMargin()
    {
        Assert.False(TargetTrackPolicy.ShouldSwitch(
            currentScore: 0.70,
            challengerScore: 0.75,
            challengerWinningFrames: 2));
        Assert.True(TargetTrackPolicy.ShouldSwitch(
            currentScore: 0.70,
            challengerScore: 0.75,
            challengerWinningFrames: 3));
    }

    [Fact]
    public void LargeScoreMarginCanSwitchImmediately()
    {
        Assert.True(TargetTrackPolicy.ShouldSwitch(
            currentScore: 0.55,
            challengerScore: 0.85,
            challengerWinningFrames: 1));
    }

    [Fact]
    public void MissingTrackIsRetainedOnlyInsideTheShortLossWindow()
    {
        var seenAt = DateTime.UtcNow;

        Assert.True(TargetTrackPolicy.ShouldRetainMissing(
            seenAt,
            seenAt.AddMilliseconds(800)));
        Assert.False(TargetTrackPolicy.ShouldRetainMissing(
            seenAt,
            seenAt.AddMilliseconds(1000)));
    }

    [Fact]
    public void IndicatorCanHandOffToOneCentralHealthBarInsideTheWindow()
    {
        var turnedAt = DateTime.UtcNow;
        var motion = new PendingCameraMotion(180, 0, turnedAt);
        var health = new EnemySeekVisual(850, 320, 100, 6, 600);

        Assert.True(TargetTrackPolicy.CanHandoffIndicatorToHealth(
            health,
            imageWidth: 1920,
            competingHealthBars: 1,
            motion,
            turnedAt.AddMilliseconds(450)));
    }

    [Fact]
    public void AmbiguousOrExpiredHealthBarsCannotInheritTheTrack()
    {
        var turnedAt = DateTime.UtcNow;
        var motion = new PendingCameraMotion(180, 0, turnedAt);
        var health = new EnemySeekVisual(850, 320, 100, 6, 600);

        Assert.False(TargetTrackPolicy.CanHandoffIndicatorToHealth(
            health,
            1920,
            competingHealthBars: 2,
            motion,
            turnedAt.AddMilliseconds(450)));
        Assert.False(TargetTrackPolicy.CanHandoffIndicatorToHealth(
            health,
            1920,
            competingHealthBars: 1,
            motion,
            turnedAt.AddMilliseconds(700)));
    }

    [Fact]
    public void BackgroundMotionAloneIsNotTargetProgress()
    {
        Assert.False(TargetTrackPolicy.HasTargetProgress(
            bearingImproved: false,
            healthBarImproved: false,
            confidenceImproved: false,
            handoffSucceeded: false,
            actuationObserved: true));
    }

    [Theory]
    [InlineData(1199, 3, true)]
    [InlineData(1200, 3, false)]
    [InlineData(1000, 4, false)]
    public void IndicatorOnlyMovementHasACumulativeCap(
        int cumulativeMilliseconds,
        int pulses,
        bool expected)
    {
        Assert.Equal(
            expected,
            TargetTrackPolicy.CanContinueIndicatorOnly(
                TimeSpan.FromMilliseconds(cumulativeMilliseconds),
                pulses));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    public void ThreeSlicesWithoutProgressReturnNoProgress(
        int consecutiveSlices,
        bool expected)
    {
        Assert.Equal(expected, TargetTrackPolicy.IsNoProgress(consecutiveSlices));
    }

    private static EnemySeekDecision HealthDecision(EnemySeekVisual visual)
    {
        return new EnemySeekDecision(
            AutoFightSeekAction.ApproachVisibleEnemy,
            EnemyIndicatorDirection.None,
            visual,
            SignalCount: 1);
    }
}
