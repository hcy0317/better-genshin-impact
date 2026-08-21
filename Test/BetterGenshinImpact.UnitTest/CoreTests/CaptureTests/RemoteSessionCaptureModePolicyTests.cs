using Fischless.GameCapture;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class RemoteSessionCaptureModePolicyTests
{
    [Theory]
    [InlineData(CaptureModes.WindowsGraphicsCapture)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureHdr)]
    public void Resolve_ShouldUseBitBltForWindowsGraphicsCaptureInsideRemoteSession(CaptureModes requestedMode)
    {
        var resolvedMode = RemoteSessionCaptureModePolicy.Resolve(requestedMode, isRemoteSession: true);

        Assert.Equal(CaptureModes.BitBlt, resolvedMode);
    }

    [Theory]
    [InlineData(CaptureModes.BitBlt, true)]
    [InlineData(CaptureModes.DwmGetDxSharedSurface, true)]
    [InlineData(CaptureModes.WindowsGraphicsCapture, false)]
    [InlineData(CaptureModes.WindowsGraphicsCaptureHdr, false)]
    public void Resolve_ShouldPreserveModesThatDoNotNeedRemoteFallback(
        CaptureModes requestedMode,
        bool isRemoteSession)
    {
        var resolvedMode = RemoteSessionCaptureModePolicy.Resolve(requestedMode, isRemoteSession);

        Assert.Equal(requestedMode, resolvedMode);
    }
}
