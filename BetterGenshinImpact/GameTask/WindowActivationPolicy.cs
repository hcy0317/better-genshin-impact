using System;

namespace BetterGenshinImpact.GameTask;

internal static class WindowActivationPolicy
{
    internal static void Execute(
        uint currentThreadId,
        uint foregroundThreadId,
        uint targetThreadId,
        Func<uint, uint, bool, bool> attachThreadInput,
        Action activate)
    {
        ArgumentNullException.ThrowIfNull(attachThreadInput);
        ArgumentNullException.ThrowIfNull(activate);

        var foregroundAttached = false;
        var targetAttached = false;

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                foregroundAttached = attachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (targetThreadId != 0 &&
                targetThreadId != currentThreadId &&
                targetThreadId != foregroundThreadId)
            {
                targetAttached = attachThreadInput(currentThreadId, targetThreadId, true);
            }

            activate();
        }
        finally
        {
            if (targetAttached)
            {
                _ = attachThreadInput(currentThreadId, targetThreadId, false);
            }

            if (foregroundAttached)
            {
                _ = attachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }
}
