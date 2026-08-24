using BetterGenshinImpact.Service;
using Fischless.WindowsInput;
using System;

namespace BetterGenshinImpact.GameTask.GameLoading;

internal static class GameLoadingInputPolicy
{
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
