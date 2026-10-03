using BetterGenshinImpact.GameTask.Common.Ui;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ReviveNativeClickTests
{
    [Fact]
    public async Task ReviveUsesTheCurrentButtonAndAConfirmedForegroundClick()
    {
        using var fixture = new DomainTipNativeFixture
        {
            Scene = new(1) { Revive = true, FullPartyDefeat = true, DefeatOverlay = true,
                ReviveButtonBounds = new Rect(910, 970, 100, 30) },
            Title = false, Footer = false, OnAction = _ => true
        };
        var observed = fixture.Driver.Capture();
        Assert.True(await fixture.Driver.ActAsync(UiAction.ReviveParty, observed, default));
        Assert.Equal(new Rect(910, 970, 100, 30), Assert.Single(fixture.Clicks).Bounds);
        Assert.Empty(fixture.Actions);
        Assert.Equal(new[] { "move", "down", "up" }, fixture.NativeStages);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task DefeatClassificationWithoutAButtonCannotAuthorizeAClick()
    {
        using var fixture = new DomainTipNativeFixture
        {
            Scene = new(1) { Revive = true, FullPartyDefeat = true },
            Title = false, Footer = false, OnAction = _ => true
        };
        Assert.False(await fixture.Driver.ActAsync(UiAction.ReviveParty, fixture.Driver.Capture(), default));
        Assert.Empty(fixture.Clicks);
        Assert.Empty(fixture.Actions);
    }
}
