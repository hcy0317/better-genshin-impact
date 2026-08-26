using BetterGenshinImpact.GameTask.GameLoading;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class GameLoadingInputPolicyTests
{
    [Fact]
    public void TryClick_ShouldUseRecognizedTargetWhenAvailable()
    {
        var prepared = false;
        var recognizedClicked = false;
        var centerClicked = false;

        var succeeded = GameLoadingInputPolicy.TryClick(
            hasRecognizedTarget: true,
            prepareInput: () => prepared = true,
            clickRecognizedTarget: () => recognizedClicked = true,
            clickCenterFallback: () => centerClicked = true,
            out var failure);

        Assert.True(succeeded);
        Assert.True(prepared);
        Assert.True(recognizedClicked);
        Assert.False(centerClicked);
        Assert.Null(failure);
    }

    [Fact]
    public void TryClick_ShouldUseGameCenterWhenRecognitionIsUnavailable()
    {
        var prepared = false;
        var recognizedClicked = false;
        var centerClicked = false;

        var succeeded = GameLoadingInputPolicy.TryClick(
            hasRecognizedTarget: false,
            prepareInput: () => prepared = true,
            clickRecognizedTarget: () => recognizedClicked = true,
            clickCenterFallback: () => centerClicked = true,
            out var failure);

        Assert.True(succeeded);
        Assert.True(prepared);
        Assert.False(recognizedClicked);
        Assert.True(centerClicked);
        Assert.Null(failure);
    }

    [Fact]
    public void TryClick_ShouldKeepWaitingWhenRdpInputDispatchTemporarilyFails()
    {
        var inputFailure = new InputDispatchException("SendInput temporarily unavailable");

        var succeeded = GameLoadingInputPolicy.TryClick(
            hasRecognizedTarget: true,
            prepareInput: () => { },
            clickRecognizedTarget: () => throw inputFailure,
            clickCenterFallback: () => { },
            out var observedFailure);

        Assert.False(succeeded);
        Assert.Same(inputFailure, observedFailure);
    }

    [Theory]
    [InlineData(false, false, false, 21, 16, 0, true)]
    [InlineData(false, false, true, 300, 300, 0, false)]
    [InlineData(false, false, false, 300, 300, 3, false)]
    [InlineData(true, false, false, 300, 300, 0, false)]
    [InlineData(false, true, false, 10, 5, 2, true)]
    public void ShouldUseCenterFallback_BoundsBlindClicksAndStopsAfterRecognizedDoorClick(
        bool hasBlockingPrompt,
        bool hasDoorText,
        bool recognizedDoorClickSucceeded,
        int elapsedSeconds,
        int secondsSinceLastFallback,
        int fallbackClickCount,
        bool expected)
    {
        var actual = GameLoadingInputPolicy.ShouldUseCenterFallback(
            hasBlockingPrompt,
            hasDoorText,
            recognizedDoorClickSucceeded,
            TimeSpan.FromSeconds(elapsedSeconds),
            TimeSpan.FromSeconds(secondsSinceLastFallback),
            fallbackClickCount);

        Assert.Equal(expected, actual);
    }
}
