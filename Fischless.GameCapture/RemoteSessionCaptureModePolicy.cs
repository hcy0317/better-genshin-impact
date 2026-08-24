using System.Runtime.InteropServices;

namespace Fischless.GameCapture;

internal static class RemoteSessionCaptureModePolicy
{
    public static CaptureModes Resolve(CaptureModes requestedMode, bool isRemoteSession)
    {
        if (!isRemoteSession)
        {
            return requestedMode;
        }

        return requestedMode is CaptureModes.WindowsGraphicsCapture or CaptureModes.WindowsGraphicsCaptureHdr
            ? CaptureModes.BitBlt
            : requestedMode;
    }
}

internal static class RemoteSessionDetector
{
    private const int SmRemoteSession = 0x1000;

    public static bool IsRemoteSession()
    {
        return GetSystemMetrics(SmRemoteSession) != 0;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
