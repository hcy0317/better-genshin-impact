using Fischless.WindowsInput;

namespace BetterGenshinImpact.UnitTest.CoreTests.InputTests;

public class WindowsInputMessageDispatcherTests
{
    [Fact]
    public void SubmissionCaptureReportsNativeCountsAndChecksAfterWindowPreparation()
    {
        var prepared = false;
        var nativeCalls = 0;
        using var capture = new InputDispatchCapture(() => Assert.True(prepared));
        var dispatcher = new WindowsInputMessageDispatcher(() => prepared = true,
            input => { nativeCalls++; return (uint)input.Length; }, () => 0);
        dispatcher.DispatchInput(new Vanara.PInvoke.User32.INPUT[2]);
        Assert.Equal(1, nativeCalls);
        Assert.Equal(1, capture.NativeCalls);
        Assert.Equal(2, capture.Requested);
        Assert.Equal(2, capture.Submitted);
        Assert.False(capture.Uncertain);
    }

    [Fact]
    public void ARejectedLastMomentAdmissionNeverReachesTheNativeSender()
    {
        var prepared = false;
        var calls = 0;
        var original = new TimeoutException("frame expired during focus preparation");
        using var capture = new InputDispatchCapture(() =>
        {
            Assert.True(prepared);
            throw original;
        });
        var dispatcher = new WindowsInputMessageDispatcher(() => prepared = true,
            inputs => { calls++; return (uint)inputs.Length; }, () => 0);
        Assert.Same(original, Assert.Throws<TimeoutException>(() => dispatcher.DispatchInput(new Vanara.PInvoke.User32.INPUT[2])));
        Assert.Equal(0, calls);
        Assert.Equal(0, capture.NativeCalls);
        Assert.Equal(0, capture.Submitted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void PartialAndZeroSubmissionsKeepTheirActualNativeCounts(uint sent)
    {
        using var capture = new InputDispatchCapture();
        var dispatcher = new WindowsInputMessageDispatcher(null, _ => sent, () => 5);
        Assert.Throws<InputDispatchException>(() => dispatcher.DispatchInput(new Vanara.PInvoke.User32.INPUT[2]));
        Assert.Equal(1, capture.NativeCalls);
        Assert.Equal(2, capture.Requested);
        Assert.Equal((int)sent, capture.Submitted);
        Assert.False(capture.Uncertain);
    }

    [Fact]
    public void AThrowingNativeBoundaryIsUnknownAndDoesNotInventASentCount()
    {
        var original = new InvalidOperationException("native boundary did not return a count");
        using var capture = new InputDispatchCapture();
        var dispatcher = new WindowsInputMessageDispatcher(null, _ => throw original, () => 0);
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => dispatcher.DispatchInput(new Vanara.PInvoke.User32.INPUT[2])));
        Assert.Equal(1, capture.NativeCalls);
        Assert.Equal(0, capture.Submitted);
        Assert.True(capture.Uncertain);
    }

    [Fact]
    public void InvokeBeforeInputDispatch_RunsConfiguredGuard()
    {
        var invocationCount = 0;
        var dispatcher = new WindowsInputMessageDispatcher(() => invocationCount++);

        dispatcher.InvokeBeforeInputDispatch();

        Assert.Equal(1, invocationCount);
    }

    [Fact]
    public void FailureMessageContainsNativeDiagnosticsAndUipiCaveat()
    {
        var message = WindowsInputMessageDispatcher.CreateFailureMessage(
            requested: 3,
            sent: 0,
            errorCode: 5,
            errorMessage: "Access is denied.",
            processId: 1234,
            sessionId: 7);

        Assert.Contains("requested=3", message);
        Assert.Contains("sent=0", message);
        Assert.Contains("win32Error=5", message);
        Assert.Contains("Access is denied.", message);
        Assert.Contains("pid=1234", message);
        Assert.Contains("sessionId=7", message);
        Assert.Contains("UIPI", message);
        Assert.Contains("GetLastError", message);
    }
}
