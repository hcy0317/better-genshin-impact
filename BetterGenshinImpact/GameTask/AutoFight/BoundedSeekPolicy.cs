using System;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal static class BoundedSeekPolicy
{
    internal static readonly TimeSpan MaximumBudget = TimeSpan.FromMilliseconds(600);
    internal static readonly TimeSpan MinimumMovementDuration = TimeSpan.FromMilliseconds(200);
    internal static readonly TimeSpan MaximumMovementDuration = TimeSpan.FromMilliseconds(350);

    internal static TimeSpan NormalizeBudget(TimeSpan requested)
    {
        return requested <= TimeSpan.Zero
            ? TimeSpan.FromMilliseconds(1)
            : requested > MaximumBudget
                ? MaximumBudget
                : requested;
    }

    internal static TimeSpan NormalizeMovementDuration(TimeSpan requested)
    {
        return requested < MinimumMovementDuration
            ? MinimumMovementDuration
            : requested > MaximumMovementDuration
                ? MaximumMovementDuration
                : requested;
    }

    internal static bool CanRequestMovement(
        AutoFightSeekAction action,
        bool isFixedTopHealth)
    {
        return !isFixedTopHealth &&
               action is AutoFightSeekAction.Approach or AutoFightSeekAction.ContinueLockedRoute;
    }

    internal static bool ShouldUseLegacySeekPath(CombatTargetingMode mode)
    {
        return mode == CombatTargetingMode.Legacy;
    }

    internal static bool CanMoveAfterCameraPulse(
        int horizontalOffset,
        int verticalOffset)
    {
        return horizontalOffset == 0 && verticalOffset == 0;
    }

    internal static bool CanSeekAtTxtRoundBoundary(
        int commandIndex,
        int commandCount,
        bool beforeCommand)
    {
        if (commandCount <= 0 || commandIndex < 0 || commandIndex >= commandCount)
        {
            return false;
        }
        return beforeCommand
            ? commandIndex == 0
            : commandIndex == commandCount - 1;
    }

    internal static bool CanSeekAtJsonActionBoundary(bool actionCompleted)
    {
        return actionCompleted;
    }
}
