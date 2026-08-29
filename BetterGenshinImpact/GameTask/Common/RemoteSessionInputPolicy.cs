using System;

namespace BetterGenshinImpact.GameTask.Common;

internal static class RemoteSessionInputPolicy
{
    internal static bool ShouldActivateWithoutForegroundVerification(
        bool isTerminalServerSession,
        string? activeProcessName)
    {
        return isTerminalServerSession &&
               string.Equals(activeProcessName, "Idle", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool ShouldActivateBeforeInput(
        bool isTerminalServerSession,
        bool isTaskContextInitialized,
        bool isGameWindowActive,
        long now,
        long lastActivation,
        long activationIntervalMilliseconds)
    {
        return isTerminalServerSession &&
               isTaskContextInitialized &&
               !isGameWindowActive &&
               activationIntervalMilliseconds >= 0 &&
               now - lastActivation >= activationIntervalMilliseconds;
    }

    internal static bool ShouldDismissTransientShellWindow(
        bool isTerminalServerSession,
        string? activeProcessName)
    {
        return isTerminalServerSession &&
               string.Equals(activeProcessName, "SearchHost", StringComparison.OrdinalIgnoreCase);
    }
}
