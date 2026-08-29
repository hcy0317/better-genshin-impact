using System;
using System.Linq;
using System.Runtime.CompilerServices;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask;

internal static class TaskFailureDiagnostics
{
    private sealed class CaptureMarker
    {
    }

    private static readonly object CaptureLock = new();
    private static readonly ConditionalWeakTable<Exception, CaptureMarker> CapturedFailures = new();

    internal static void CaptureScreenshotOnce(
        Exception failure,
        string context,
        Action<string>? capture = null)
    {
        if (failure is OperationCanceledException or NormalEndException)
        {
            return;
        }

        lock (CaptureLock)
        {
            if (WasCaptured(failure))
            {
                return;
            }
            CapturedFailures.Add(failure, new CaptureMarker());
        }

        try
        {
            (capture ?? (reason => TaskTriggerDispatcher.Instance().TakeFailureScreenshot(reason)))(context);
        }
        catch (Exception)
        {
            // TakeFailureScreenshot 会记录 Error/Debug；此层只保证诊断失败不覆盖原始异常。
        }
    }

    private static bool WasCaptured(Exception failure)
    {
        if (CapturedFailures.TryGetValue(failure, out _))
        {
            return true;
        }
        if (failure is AggregateException aggregateException &&
            aggregateException.Flatten().InnerExceptions.Any(WasCaptured))
        {
            return true;
        }
        return failure.InnerException is not null && WasCaptured(failure.InnerException);
    }
}
