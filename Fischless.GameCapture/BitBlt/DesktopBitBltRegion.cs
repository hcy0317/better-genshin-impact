using Vanara.PInvoke;

namespace Fischless.GameCapture.BitBlt;

internal static class DesktopBitBltRegion
{
    internal static bool TryCreate(POINT origin, int clientWidth, int clientHeight,
        int bufferWidth, int bufferHeight, RECT desktop, out RECT region)
    {
        region = default;
        if (clientWidth <= 0 || clientHeight <= 0 ||
            clientWidth != bufferWidth || clientHeight != bufferHeight ||
            origin.X < desktop.Left || origin.Y < desktop.Top ||
            (long)origin.X + clientWidth > desktop.Right ||
            (long)origin.Y + clientHeight > desktop.Bottom) return false;
        region = new RECT(origin.X, origin.Y, origin.X + clientWidth, origin.Y + clientHeight);
        return true;
    }
}
