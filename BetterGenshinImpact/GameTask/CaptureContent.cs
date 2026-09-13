using BetterGenshinImpact.GameTask.Model.Area;
using System;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask;

/// <summary>
/// 捕获的内容
/// 以及一些多个trigger会用到的内容
/// </summary>
public class CaptureContent : IDisposable
{
    public static readonly int MaxFrameIndexSecond = 60;
    public int FrameIndex { get; }
    public double TimerInterval { get; }

    public int FrameRate => (int)(1000 / TimerInterval);

    public ImageRegion CaptureRectArea { get; }
    
    public GameUiCategory CurrentGameUiCategory;

    public CaptureContent(Mat image, int frameIndex, double interval)
        : this(image, frameIndex, interval, TaskContext.Instance().SystemInfo, default) { }

    public CaptureContent(GameCaptureFrame frame, int frameIndex, double interval)
        : this(frame, frameIndex, interval, TaskContext.Instance().SystemInfo) { }

    internal CaptureContent(GameCaptureFrame frame, int frameIndex, double interval, ISystemInfo systemInfo)
        : this(frame.Frame, frameIndex, interval, systemInfo, frame.Stamp) { }

    private CaptureContent(Mat image, int frameIndex, double interval, ISystemInfo systemInfo, CaptureFrameStamp stamp)
    {
        FrameIndex = frameIndex;
        TimerInterval = interval;
        GameCaptureRegion? gameCaptureRegion = null;
        try
        {
            gameCaptureRegion = systemInfo.DesktopRectArea.Derive(image, systemInfo.CaptureAreaRect.X, systemInfo.CaptureAreaRect.Y);
            gameCaptureRegion.FrameStamp = stamp;
            CaptureRectArea = gameCaptureRegion.DeriveTo1080P();
        }
        catch
        {
            if (gameCaptureRegion != null) gameCaptureRegion.Dispose();
            else image.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 用于兼容新的 ImageRegion
    /// </summary>
    /// <param name="ra"></param>
    public CaptureContent(ImageRegion ra)
    {
        CaptureRectArea = ra;
    }

    public void Dispose()
    {
        CaptureRectArea.Dispose();
        GC.SuppressFinalize(this);
    }
}
