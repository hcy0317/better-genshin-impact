namespace Fischless.GameCapture.Graphics;

internal static class CaptureShutdownCoordinator
{
    public static void Stop(
        FrameCallbackLifetime callbackLifetime,
        IReadOnlyList<(string Name, Action Action)> quiesceSteps,
        IReadOnlyList<(string Name, Action Action)> releaseSteps)
    {
        List<Exception> failures = [];

        callbackLifetime.BeginStop();
        RunAll(quiesceSteps, failures);
        callbackLifetime.WaitForCallbacks();
        RunAll(releaseSteps, failures);

        if (failures.Count > 0)
        {
            throw new AggregateException("停止 Windows Graphics Capture 时清理失败。", failures);
        }
    }

    private static void RunAll(
        IReadOnlyList<(string Name, Action Action)> steps,
        ICollection<Exception> failures)
    {
        foreach (var (_, action) in steps)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failures.Add(exception);
            }
        }
    }
}
