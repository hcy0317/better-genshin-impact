using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ClimbDetachNativeDriverTests
{
    private static UiSnapshot Climb => new(1) { MainHud = true, World = new(true, false, false)
    { OrdinaryAvatarHud = true, ControlObserved = true, Motion = MotionStatus.Climb } };

    [Fact]
    public async Task ConfirmedSameSessionSuccessorCanDetachWithoutTeleporting()
    {
        using var fixture = new DomainTipNativeFixture { Scene = Climb, Title = false, Footer = false, OnAction = _ => true };
        var observed = fixture.Driver.Capture();
        Assert.True(await fixture.Driver.ActAsync(UiAction.DetachClimb, observed, default));
        Assert.Equal(new[] { UiAction.DetachClimb }, fixture.Actions);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("transformed")]
    [InlineData("breakout")]
    [InlineData("rejected")]
    [InlineData("unknown")]
    [InlineData("fly")]
    [InlineData("same-frame")]
    [InlineData("session")]
    [InlineData("stale")]
    public async Task ChangedOrUnusableEvidenceCannotDetach(string kind)
    {
        using var fixture = new DomainTipNativeFixture { Scene = Climb, Title = false, Footer = false, OnAction = _ => true };
        var observed = fixture.Driver.Capture();
        fixture.Scene = kind switch
        {
            "map" => new(1) { BigMap = true },
            "transformed" => Climb with { World = Climb.World!.Value with { Transformed = true } },
            "breakout" => Climb with { World = Climb.World!.Value with { KeyboardBreakout = true } },
            "rejected" => Climb with { World = Climb.World!.Value with { PartyRejected = true } },
            "unknown" => Climb with { World = Climb.World!.Value with { ControlObserved = false } },
            "fly" => Climb with { World = Climb.World!.Value with { Motion = MotionStatus.Fly } },
            _ => Climb
        };
        if (kind == "same-frame") fixture.SourceOverride = _ => observed.SourceStamp;
        if (kind == "session") fixture.SourceOverride = source => source with { SessionId = Guid.NewGuid() };
        if (kind == "stale") fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.False(await fixture.Driver.ActAsync(UiAction.DetachClimb, observed, default));
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public async Task EvidenceExpiringAtTransportCannotSendInput()
    {
        using var fixture = new DomainTipNativeFixture { Scene = Climb, Title = false, Footer = false, OnAction = _ => true };
        var observed = fixture.Driver.Capture();
        fixture.BeforeActionTransport = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Driver.ActAsync(UiAction.DetachClimb, observed, default));
        Assert.Empty(fixture.Actions);
    }
}
