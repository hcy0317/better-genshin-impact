using System;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common;

internal static class GameCaptureRetry
{
    internal static Mat Capture(
        IGameCapture? gameCapture,
        Action<int> retryDelay,
        Action<string>? logWarning = null)
    {
        var captureFrame = gameCapture?.Capture();
        var image = captureFrame?.Frame;
        if (image != null)
        {
            return image;
        }

        captureFrame?.Dispose();
        logWarning?.Invoke("截图失败!");

        for (var i = 0; i < 3; i++)
        {
            captureFrame = gameCapture?.Capture();
            image = captureFrame?.Frame;
            if (image != null)
            {
                return image;
            }

            captureFrame?.Dispose();
            // 截图失败通常发生在游戏/截图器退出阶段。这里只允许无副作用的短等待，
            // 不能进入窗口焦点恢复或任务暂停逻辑。
            retryDelay(30);
        }

        throw new Exception("尝试多次后,截图失败!");
    }
}
