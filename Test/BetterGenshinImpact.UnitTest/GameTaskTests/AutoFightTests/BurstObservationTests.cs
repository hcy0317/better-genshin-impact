using BetterGenshinImpact.GameTask.AutoFight.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class BurstObservationTests
{
    [Fact]
    public void ExpiredAdmissionAfterRecognitionNeverSendsBurst()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var deadline = clock.GetUtcNow().AddSeconds(1);
        var presses = 0;
        var result = BurstCastProtocol.TryCast(() =>
            {
                clock.Advance(TimeSpan.FromSeconds(1.1));
                return new(true, false);
            }, () => presses++, _ => { }, CancellationToken.None, clock,
            tryBeginInput: () => clock.GetUtcNow() < deadline);
        Assert.Equal(0, presses);
        Assert.Equal(BurstCastResult.Unknown, result);
    }

    [Fact]
    public void SlowRecognitionConsumesTheSameOverallConfirmationBudget()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var observations = 0;
        var result = BurstCastProtocol.TryCast(() =>
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                return ++observations == 1 ? new(true, false) : default;
            }, () => { }, ms => clock.Advance(TimeSpan.FromMilliseconds(ms)), CancellationToken.None, clock);
        Assert.Equal(BurstCastResult.Unconfirmed, result);
        Assert.InRange(observations, 2, 3);
    }

    [Theory]
    [InlineData(null, .99)]
    [InlineData("energy 1 cd 0", .5)]
    [InlineData("energy_h_cd_1", .7)]
    [InlineData("energy 2 cd 1", .99)]
    [InlineData("energy h cd 2", .99)]
    [InlineData("energy h cd 1 extra", .99)]
    [InlineData("energy 1 cd 0", double.NaN)]
    [InlineData("energy 1 cd 0", double.PositiveInfinity)]
    [InlineData("unexpected cd 1", .99)]
    public void UncertainClassificationDoesNotInventEnergyOrCooldown(string? label, double confidence)
    {
        var state = BurstObservation.FromClassifier(label, confidence);
        Assert.Null(state.EnergyFull);
        Assert.Null(state.CoolingDown);
        Assert.False(state.Ready);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(null, null)]
    [InlineData(null, false)]
    [InlineData(null, true)]
    public void NonReadyStateNeverSendsQ(bool? energy, bool? cooling)
    {
        var presses = 0;
        var result = BurstCastProtocol.TryCast(() => new(energy, cooling), () => presses++, _ => { }, CancellationToken.None);
        Assert.NotEqual(BurstCastResult.Confirmed, result);
        Assert.Equal(0, presses);
    }

    [Fact]
    public void MissingUiAfterPressIsNotSuccessfulBurstEvidence()
    {
        var observations = 0;
        var presses = 0;
        var result = BurstCastProtocol.TryCast(() => ++observations == 1 ? new(true, false) : default,
            () => presses++, _ => { }, CancellationToken.None);
        Assert.Equal(BurstCastResult.Unconfirmed, result);
        Assert.Equal(1, presses);
        Assert.InRange(observations, 2, 17);
    }

    [Fact]
    public void ReadyToCooldownTransitionConfirmsExactlyOneBurst()
    {
        var observations = new Queue<BurstObservation>([new(true, false), default, new(false, true)]);
        var presses = 0;
        Assert.Equal(BurstCastResult.Confirmed, BurstCastProtocol.TryCast(observations.Dequeue,
            () => presses++, _ => { }, CancellationToken.None));
        Assert.Equal(1, presses);
    }

    [Theory]
    [InlineData("energy_h_cd_1", .95, BurstCastResult.Confirmed)]
    [InlineData("energy h cd 1", .95, BurstCastResult.Confirmed)]
    [InlineData("energy_h_cd_0", .95, BurstCastResult.Unconfirmed)]
    [InlineData("energy_h_cd_1", .7, BurstCastResult.Unconfirmed)]
    [InlineData("energy invalid cd 1", .99, BurstCastResult.Unconfirmed)]
    public void OnlyReliableNewCooldownConfirmsTheSingleOriginalBurst(string label, double confidence, BurstCastResult expected)
    {
        var observations = 0;
        var presses = 0;
        var result = BurstCastProtocol.TryCast(() => ++observations == 1
                ? BurstObservation.FromClassifier("energy_1_cd_0", .99)
                : BurstObservation.FromClassifier(label, confidence),
            () => presses++, _ => { }, CancellationToken.None);
        Assert.Equal(expected, result);
        Assert.Equal(1, presses);
    }

    [Fact]
    public void CancellationDuringConfirmationStopsFurtherObservations()
    {
        using var cts = new CancellationTokenSource();
        var observations = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => BurstCastProtocol.TryCast(
            () => { observations++; return new(true, false); }, () => { }, _ => cts.Cancel(), cts.Token));
        Assert.Equal(1, observations);
    }

    [Theory]
    [InlineData("energy 0 cd 1", false, true)]
    [InlineData("energy 1 cd 1", true, true)]
    [InlineData("energy 0 cd 0", false, false)]
    [InlineData("energy 1 cd 0", true, false)]
    [InlineData("energy h cd 0", null, false)]
    [InlineData("energy h cd 1", null, true)]
    [InlineData("energy_0_cd_0", false, false)]
    [InlineData("energy_0_cd_1", false, true)]
    [InlineData("energy_1_cd_0", true, false)]
    [InlineData("energy_1_cd_1", true, true)]
    [InlineData("energy_h_cd_0", null, false)]
    [InlineData("energy_h_cd_1", null, true)]
    public void EnergyAndCooldownAreIndependent(string label, bool? full, bool cooling)
    {
        var state = BurstObservation.FromClassifier(label, .95);
        Assert.Equal(full, state.EnergyFull);
        Assert.Equal(cooling, state.CoolingDown);
        Assert.Equal(full == true && !cooling, state.Ready);
    }
}
