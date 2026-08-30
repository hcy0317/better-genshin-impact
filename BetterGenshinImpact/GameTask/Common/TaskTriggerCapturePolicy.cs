namespace BetterGenshinImpact.GameTask.Common;

internal static class TaskTriggerCapturePolicy
{
    internal static bool ShouldSkip(
        bool gameActive,
        bool independentTaskRunning,
        bool hasEnabledTriggers) =>
        gameActive && independentTaskRunning && !hasEnabledTriggers;
}
