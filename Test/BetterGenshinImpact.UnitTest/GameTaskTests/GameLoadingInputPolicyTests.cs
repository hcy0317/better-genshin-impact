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
}
