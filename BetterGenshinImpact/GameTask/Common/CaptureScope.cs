using System;

namespace BetterGenshinImpact.GameTask.Common;

internal static class CaptureScope
{
    public static TResult Use<TCapture, TResult>(TCapture capture, Func<TCapture, TResult> action)
        where TCapture : IDisposable
    {
        using (capture)
        {
            return action(capture);
        }
    }
}
