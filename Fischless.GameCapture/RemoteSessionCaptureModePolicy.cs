using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Fischless.GameCapture;

public static class RemoteSessionCaptureModePolicy
{
    public static CaptureModes Resolve(CaptureModes requestedMode, bool isRemoteSession)
    {
        if (!isRemoteSession)
        {
            return requestedMode;
        }

        return requestedMode is CaptureModes.WindowsGraphicsCapture or CaptureModes.WindowsGraphicsCaptureHdr or CaptureModes.WindowsGraphicsCaptureV2
            ? CaptureModes.BitBlt
            : requestedMode;
    }

    public static IGameCapture Start(CaptureModes requestedMode, bool? isRemoteSession, bool isChildSession,
        nint hWnd, IReadOnlyDictionary<string, object> settings, Func<CaptureModes, IGameCapture> create)
    {
        var mode = Resolve(requestedMode, isRemoteSession == true);
        var capture = create(mode);
        try
        {
            var runtimeSettings = new Dictionary<string, object>(settings);
            if (mode == CaptureModes.BitBlt && (isChildSession || isRemoteSession == true))
                runtimeSettings["captureFromDesktop"] = true;
            capture.Start(hWnd, runtimeSettings);
            return capture;
        }
        catch
        {
            capture.Dispose();
            throw;
        }
    }
}

public static class RemoteSessionDetector
{
    private const int SmRemoteSession = 0x1000;

    public static bool IsRemoteSession()
    {
        return GetSystemMetrics(SmRemoteSession) != 0;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
}
