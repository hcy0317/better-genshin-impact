using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DomainExitNativeTests
{
    private static DomainTipNativeFixture ExitPrompt() => new()
    {
        Scene = new(1) { MainHud = true, InDomain = true, BlackConfirm = true,
            DomainExit = new(default, true, new Rect(990, 740, 35, 35)) },
        Title = false, Footer = false
    };

    [Fact]
    public async Task TwoSlowReadsUseFreshSuccessorToConfirmExactlyOnce()
    {
        using var io = ExitPrompt();
        io.AfterCapture = () => io.Clock.Advance(TimeSpan.FromMilliseconds(1220));
        io.OnClick = () => io.Scene = new(1) { MainHud = true };
        Assert.True((await UiRecovery.ExitDomainAsync(io.Driver, default, clock: io.Clock)).Matches(UiTarget.Overworld));
        Assert.Single(io.Clicks);
        Assert.Equal(new[] { "move", "down", "up" }, io.NativeStages);
        Assert.Empty(io.Actions);
        Assert.All(io.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("focus")]
    [InlineData("current")]
    [InlineData("foreign")]
    [InlineData("repeat")]
    [InlineData("fence")]
    [InlineData("consumption")]
    [InlineData("revive")]
    [InlineData("world")]
    public async Task InvalidFramesNeverAuthorizeConfirmation(string fault)
    {
        using var io = ExitPrompt();
        var observed = io.Driver.Capture();
        switch (fault)
        {
            case "old": io.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "focus": io.OnFocus = () => io.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "current": io.AfterCapture = () => io.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "foreign": io.SourceOverride = _ => new CaptureFrameSource(io.Clock).Next(); break;
            case "repeat": io.SourceOverride = _ => observed.SourceStamp; break;
            case "fence": io.Driver.MarkInputCompleted(observed); io.SourceOverride = _ => io.Producer.Next(observed.SourceStamp.CapturedTimestamp); break;
            case "consumption": io.Scene = io.Scene with { Prompt = true, DomainExit = default }; break;
            case "revive": io.Scene = io.Scene with { Revive = true }; break;
            case "world": io.Scene = new(1) { MainHud = true }; break;
        }
        Assert.False(await io.Driver.ActAsync(UiAction.ConfirmDomainExit, observed, default));
        Assert.Empty(io.NativeStages);
        Assert.Empty(io.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePreparationStillChecksExpiryAndCancellation(bool cancel)
    {
        using var io = ExitPrompt();
        using var cts = new CancellationTokenSource();
        var observed = io.Driver.Capture();
        io.BeforeTransport = stage => { if (stage == "down") { if (cancel) cts.Cancel(); else io.Clock.Advance(TimeSpan.FromSeconds(3)); } };
        var error = await Record.ExceptionAsync(() => io.Driver.ActAsync(UiAction.ConfirmDomainExit, observed, cts.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<InvalidOperationException>(error);
        Assert.DoesNotContain("down", io.NativeStages);
    }
}
