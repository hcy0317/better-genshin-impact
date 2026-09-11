using System;
using System.Threading;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class LandingAttackGuardTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(99)]
    public void RepeatedNonFlyingMovementNeverSendsAttack(int status)
    {
        var attacks = 0;
        for (var i = 0; i < 30; i++)
            Assert.False(LandingAttackGuard.TryAttack(() => (MotionStatus)status, () => attacks++, CancellationToken.None));
        Assert.Equal(0, attacks);
    }

    [Fact]
    public void ConfirmedFlightSendsExactlyOneLandingAttack()
    {
        var attacks = 0;
        Assert.True(LandingAttackGuard.TryAttack(() => MotionStatus.Fly, () => attacks++, CancellationToken.None));
        Assert.Equal(1, attacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeOrDuringObservationPreventsAttack(bool cancelDuringObservation)
    {
        using var cancellation = new CancellationTokenSource();
        if (!cancelDuringObservation) cancellation.Cancel();
        var observed = 0;
        var attacks = 0;
        Assert.Throws<OperationCanceledException>(() => LandingAttackGuard.TryAttack(() =>
        {
            observed++;
            cancellation.Cancel();
            return MotionStatus.Fly;
        }, () => attacks++, cancellation.Token));
        Assert.Equal(cancelDuringObservation ? 1 : 0, observed);
        Assert.Equal(0, attacks);
    }

    [Fact]
    public void FailedObservationDoesNotSendAttack()
    {
        var attacks = 0;
        Assert.Throws<InvalidOperationException>(() => LandingAttackGuard.TryAttack(
            () => throw new InvalidOperationException("capture failed"), () => attacks++, CancellationToken.None));
        Assert.Equal(0, attacks);
    }
}
