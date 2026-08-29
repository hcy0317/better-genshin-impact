using BetterGenshinImpact.Service;
using Fischless.WindowsInput;
using System;

namespace BetterGenshinImpact.GameTask.GameLoading;

internal static class GameLoadingInputPolicy
{
    private const int MaxCenterFallbackClickCount = 3;

    internal static bool ShouldUseCenterFallback(
        bool hasBlockingPrompt,
        bool hasDoorText,
        bool recognizedDoorClickSucceeded,
        TimeSpan elapsed,
        TimeSpan timeSinceLastFallback,
        int fallbackClickCount)
    {
        if (hasBlockingPrompt || recognizedDoorClickSucceeded || fallbackClickCount >= MaxCenterFallbackClickCount)
        {
            return false;
        }

        var fallbackInterval = hasDoorText
            ? TimeSpan.FromSeconds(5)
            : TimeSpan.FromSeconds(15);
        var mayUseTimedFallback = elapsed >= TimeSpan.FromSeconds(20);
        return (hasDoorText || mayUseTimedFallback) && timeSinceLastFallback >= fallbackInterval;
    }

    internal static bool TryClick(
        bool hasRecognizedTarget,
        Action prepareInput,
        Action clickRecognizedTarget,
        Action clickCenterFallback,
        out InputDispatchException? failure)
    {
        ArgumentNullException.ThrowIfNull(prepareInput);
        ArgumentNullException.ThrowIfNull(clickRecognizedTarget);
        ArgumentNullException.ThrowIfNull(clickCenterFallback);

        var clickAction = hasRecognizedTarget
            ? clickRecognizedTarget
            : clickCenterFallback;

        return GameStartupInputRecovery.TryExecute(
            () =>
            {
                prepareInput();
                clickAction();
            },
            out failure);
    }
}
