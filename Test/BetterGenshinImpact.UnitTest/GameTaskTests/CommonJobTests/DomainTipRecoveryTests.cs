using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DomainTipRecoveryTests
{
    private static UiSnapshot ExitPrompt() => new(1) { MainHud = true, InDomain = true, BlackConfirm = true,
        DomainExit = new(default, true, new OpenCvSharp.Rect(990, 740, 35, 35)) };
    [Fact]
    public async Task ActualNativeDriverDismissesThenCompletesTheOriginalExitProtocol()
    {
        using var fixture = new DomainTipNativeFixture();
        var ordered = new List<UiAction>();
        var domainOpen = false;
        var inWorld = false;
        var domainCaptures = 0;
        var worldCaptures = 0;
        fixture.BeforeCapture = () => { if (domainOpen) domainCaptures++; if (inWorld) worldCaptures++; };
        fixture.OnClick = () =>
        {
            if (fixture.Scene.DomainExit.Visible)
            {
                ordered.Add(UiAction.ConfirmDomainExit);
                inWorld = true;
                fixture.Scene = new(1) { MainHud = true };
                return;
            }
            ordered.Add(UiAction.DismissDomainTip);
            fixture.Title = fixture.Footer = false;
            fixture.Scene = new(1) { MainHud = true, InDomain = true };
            domainOpen = true;
        };
        fixture.OnAction = action =>
        {
            ordered.Add(action);
            domainOpen = false;
            if (action == UiAction.ConfirmDomainExit) inWorld = true;
            fixture.Scene = action switch
            {
                UiAction.RequestDomainExit => ExitPrompt(),
                UiAction.ConfirmDomainExit => new(1) { MainHud = true },
                _ => throw new InvalidOperationException("Unexpected action in isolated exit replay.")
            };
            return true;
        };

        var result = await UiRecovery.ExitDomainAsync(fixture.Driver, default, clock: fixture.Clock);

        Assert.True(result.Matches(UiTarget.Overworld));
        Assert.Equal(new[] { UiAction.DismissDomainTip, UiAction.RequestDomainExit, UiAction.ConfirmDomainExit }, ordered);
        Assert.Equal(2, fixture.Clicks.Count);
        Assert.Equal(3, domainCaptures); // Two decision frames plus Native.Act's fresh recheck.
        Assert.Equal(2, worldCaptures);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ToMainKeepsItsOriginalTargetAfterDismissingTheTip(bool requireOverworld)
    {
        using var fixture = new DomainTipNativeFixture();
        fixture.OnClick = () => { fixture.Title = fixture.Footer = false; };
        if (requireOverworld)
            await Assert.ThrowsAsync<TimeoutException>(() => UiRecovery.ToMainAsync(fixture.Driver, default, true, clock: fixture.Clock));
        else
        {
            var result = await UiRecovery.ToMainAsync(fixture.Driver, default, clock: fixture.Clock);
            Assert.True(result.Matches(UiTarget.DomainMain));
        }
        Assert.Single(fixture.Clicks);
        Assert.Empty(fixture.Actions); // ToMain does not invent an exit-domain request.
    }

    [Theory]
    [InlineData("title-only")]
    [InlineData("footer-only")]
    [InlineData("persistent")]
    [InlineData("unknown-after-dismiss")]
    [InlineData("duplicate")]
    public async Task UnresolvedTipStatesKeepTheOriginalFiniteFailure(string state)
    {
        using var fixture = new DomainTipNativeFixture();
        var started = fixture.Clock.GetUtcNow();
        if (state == "title-only") fixture.Footer = false;
        if (state == "footer-only") fixture.Title = false;
        if (state == "unknown-after-dismiss") fixture.OnClick = () =>
        { fixture.Title = fixture.Footer = false; fixture.Scene = new(1); };
        if (state == "duplicate")
        {
            Fischless.GameCapture.CaptureFrameStamp? first = null;
            fixture.SourceOverride = source => { first ??= source; return first.Value; };
        }
        await Assert.ThrowsAsync<TimeoutException>(() => UiRecovery.ExitDomainAsync(fixture.Driver, default, clock: fixture.Clock));
        Assert.InRange((fixture.Clock.GetUtcNow() - started).TotalSeconds, 20, 20.5);
        Assert.Equal(state == "persistent" ? 8 : state == "unknown-after-dismiss" ? 1 : 0, fixture.Clicks.Count);
        Assert.Empty(fixture.Actions);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownConflictingPageClosesBeforeItsBackgroundTip(bool map)
    {
        using var fixture = new DomainTipNativeFixture
        { Scene = map ? new(1) { BigMap = true } : new(1) { Prompt = true, BlackConfirm = true } };
        var ordered = new List<UiAction>();
        fixture.OnAction = action =>
        {
            ordered.Add(action);
            Assert.Equal(UiAction.Escape, action);
            fixture.Scene = new(1) { MainHud = true, InDomain = true };
            return true;
        };
        fixture.OnClick = () =>
        {
            ordered.Add(UiAction.DismissDomainTip);
            fixture.Title = fixture.Footer = false;
        };
        Assert.True((await UiRecovery.ToMainAsync(fixture.Driver, default, clock: fixture.Clock)).MainReady);
        Assert.Equal(new[] { UiAction.Escape, UiAction.DismissDomainTip }, ordered);
    }

    [Fact]
    public async Task PartialTipBreaksTheTwoConsecutiveDomainFrameRequirement()
    {
        using var fixture = new DomainTipNativeFixture { Title = false, Footer = false };
        var captures = 0;
        var requestedAt = 0;
        fixture.BeforeCapture = () =>
        {
            captures++;
            if (captures == 2) fixture.Title = true;
            if (captures == 3) fixture.Title = false;
        };
        fixture.OnAction = action =>
        {
            if (action == UiAction.RequestDomainExit)
            {
                requestedAt = captures;
                fixture.Scene = ExitPrompt();
            }
            else if (action == UiAction.ConfirmDomainExit) fixture.Scene = new(1) { MainHud = true };
            else throw new InvalidOperationException("Unexpected isolated action.");
            return true;
        };
        fixture.OnClick = () => fixture.Scene = new(1) { MainHud = true };
        Assert.True((await UiRecovery.ExitDomainAsync(fixture.Driver, default, clock: fixture.Clock)).Matches(UiTarget.Overworld));
        Assert.Equal(5, requestedAt);
        Assert.Single(fixture.Clicks);
    }

    [Fact]
    public async Task DismissDoesNotResetTheExistingExitRequestOrConfirmation()
    {
        using var fixture = new DomainTipNativeFixture { Title = false, Footer = false };
        var ordered = new List<UiAction>();
        var confirmed = false;
        var lingeringPrompt = -1;
        fixture.BeforeCapture = () =>
        {
            if (lingeringPrompt >= 0 && --lingeringPrompt < 0) fixture.Scene = new(1) { MainHud = true };
        };
        fixture.OnAction = action =>
        {
            ordered.Add(action);
            if (action == UiAction.ConfirmDomainExit) confirmed = true;
            else Assert.Equal(UiAction.RequestDomainExit, action);
            fixture.Title = fixture.Footer = true;
            fixture.Scene = new(1); // A tip overlays the pending request or completed confirmation.
            return true;
        };
        fixture.OnClick = () =>
        {
            if (fixture.Scene.DomainExit.Visible)
            {
                ordered.Add(UiAction.ConfirmDomainExit);
                confirmed = true;
                fixture.Title = fixture.Footer = true;
                fixture.Scene = new(1);
                return;
            }
            ordered.Add(UiAction.DismissDomainTip);
            fixture.Scene = ExitPrompt();
            // Before confirmation the prompt wins even while tip words remain behind it.
            if (confirmed) { fixture.Title = fixture.Footer = false; lingeringPrompt = 3; }
        };
        Assert.True((await UiRecovery.ExitDomainAsync(fixture.Driver, default, clock: fixture.Clock)).Matches(UiTarget.Overworld));
        Assert.Equal(new[] { UiAction.RequestDomainExit, UiAction.DismissDomainTip, UiAction.ConfirmDomainExit, UiAction.DismissDomainTip }, ordered);
        Assert.Equal(new[] { UiAction.RequestDomainExit }, fixture.Actions);
    }
}
