using System;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal static class AreaSelectionClickController
{
    internal static async Task<bool> TryApplyAsync(
        int maxAttempts,
        Func<int, Task<bool>> clickAndConfirmAsync)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);
        ArgumentNullException.ThrowIfNull(clickAndConfirmAsync);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            if (await clickAndConfirmAsync(attempt))
            {
                return true;
            }
        }

        return false;
    }
}
