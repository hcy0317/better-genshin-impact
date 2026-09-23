using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.WindowsInput;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class UiEscapeInputTests
{
    [Fact]
    public void SuccessfulEscapeUsesA35msPulseWithTwoNativeReceipts()
    {
        var stages = new List<string>();
        var stage = "down";
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs => { stages.Add(stage); return (uint)inputs.Length; }, () => 0);
        UiEscapeInput.Run(() => { }, () => dispatcher.DispatchInput(new User32.INPUT[1]),
            () => { stage = "up"; dispatcher.DispatchInput(new User32.INPUT[1]); },
            ms => { Assert.Equal(35, ms); stages.Add("wait"); });
        Assert.Equal(new[] { "down", "wait", "up" }, stages);
    }

    [Theory]
    [InlineData("zero")]
    [InlineData("unknown")]
    [InlineData("no-native")]
    public void IncompleteDownNeverReportsSuccessOrReplays(string fault)
    {
        var stages = new List<string>();
        var stage = "down";
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs =>
        {
            stages.Add(stage);
            if (stage == "down" && fault == "zero") return 0;
            if (stage == "down" && fault == "unknown") throw new IOException("unknown native result");
            return (uint)inputs.Length;
        }, () => 5);
        Assert.NotNull(Record.Exception(() => UiEscapeInput.Run(() => { },
            () => { if (fault != "no-native") dispatcher.DispatchInput(new User32.INPUT[1]); },
            () => { stage = "up"; dispatcher.DispatchInput(new User32.INPUT[1]); }, _ => { })));
        if (fault == "no-native") Assert.Empty(stages);
        else Assert.Equal(new[] { "down", "up" }, stages);
    }

    [Fact]
    public void NativePreparationMustPassAdmissionBeforeAnyDown()
    {
        var expired = false;
        var submitted = false;
        var released = false;
        var dispatcher = new WindowsInputMessageDispatcher(() => expired = true,
            inputs => { submitted = true; return (uint)inputs.Length; }, () => 0);
        Assert.Throws<TimeoutException>(() => UiEscapeInput.Run(
            () => { if (expired) throw new TimeoutException(); },
            () => dispatcher.DispatchInput(new User32.INPUT[1]), () => released = true, _ => { }));
        Assert.False(submitted);
        Assert.False(released);
    }

    [Fact]
    public void FailedCleanupPreservesOriginalNativeFailure()
    {
        var original = new IOException("unknown down");
        var cleanup = new IOException("unknown up");
        var stage = "down";
        var dispatcher = new WindowsInputMessageDispatcher(null, _ => throw (stage == "down" ? original : cleanup), () => 0);
        var error = Assert.Throws<AggregateException>(() => UiEscapeInput.Run(() => { },
            () => dispatcher.DispatchInput(new User32.INPUT[1]),
            () => { stage = "up"; dispatcher.DispatchInput(new User32.INPUT[1]); }, _ => { }));
        Assert.Equal(new Exception[] { original, cleanup }, error.InnerExceptions);
    }

    [Fact]
    public void CancellationAfterNativeDownStillReleasesEscape()
    {
        using var cancellation = new CancellationTokenSource();
        var stages = new List<string>();
        var stage = "down";
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs =>
        {
            stages.Add(stage);
            if (stage == "down") cancellation.Cancel();
            return (uint)inputs.Length;
        }, () => 0);
        Assert.ThrowsAny<OperationCanceledException>(() => UiEscapeInput.Run(
            cancellation.Token.ThrowIfCancellationRequested,
            () => dispatcher.DispatchInput(new User32.INPUT[1]),
            () => { stage = "up"; dispatcher.DispatchInput(new User32.INPUT[1]); },
            ms => { Assert.Equal(35, ms); cancellation.Token.ThrowIfCancellationRequested(); }));
        Assert.Equal(new[] { "down", "up" }, stages);
    }
}
