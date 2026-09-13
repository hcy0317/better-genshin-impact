using OpenCvSharp;
using Vanara.PInvoke;

namespace Fischless.GameCapture;

public sealed class GameCaptureFrame : IDisposable
{
    public GameCaptureFrame(Mat frame, RECT? captureRect = null) : this(frame, default, captureRect) { }

    public GameCaptureFrame(Mat frame, CaptureFrameStamp stamp, RECT? captureRect = null)
    {
        Frame = frame;
        CaptureRect = captureRect;
        Stamp = stamp;
    }

    public Mat Frame { get; }

    public RECT? CaptureRect { get; }

    public CaptureFrameStamp Stamp { get; }

    public GameCaptureFrame Clone() => new(Frame.Clone(), Stamp, CaptureRect);

    public void Dispose()
    {
        Frame.Dispose();
        GC.SuppressFinalize(this);
    }
}
