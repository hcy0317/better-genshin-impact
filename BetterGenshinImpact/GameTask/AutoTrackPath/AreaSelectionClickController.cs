using System;
using System.Threading.Tasks;
using System.Threading;
using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

internal readonly record struct AreaSelectionObservation(long FrameId, bool MapReady, bool SelectorOpen, bool HasCandidate);

internal static class AreaSelectionClickController
{
    internal static Task<bool> TryApplyAsync(Func<AreaSelectionObservation> capture,
        Func<int, CancellationToken, Task<bool>> clickFreshCandidate,
        Func<int, CancellationToken, Task> delay, CancellationToken ct,
        TimeProvider? clock = null, ILogger? logger = null) =>
        UiOperation.RunAsync("map-area-selection", TimeSpan.FromSeconds(10), ct, async operation =>
        {
            var clicked = false;
            var attempts = 0;
            var lastClickAt = TimeSpan.MinValue;
            long lastFrame = 0;
            var stable = 0;
            (bool, bool, bool)? lastState = null;
            while (true)
            {
                operation.Check();
                var observed = capture();
                operation.Check();
                var state = (observed.MapReady, observed.SelectorOpen, observed.HasCandidate);
                if (lastState != state)
                {
                    logger?.LogDebug("AREA_SELECTION op={Op} frame={Frame} mapReady={Map} selectorOpen={Selector} candidate={Candidate} clicked={Clicked} remainingMs={Remaining}",
                        operation.Id, observed.FrameId, observed.MapReady, observed.SelectorOpen, observed.HasCandidate,
                        clicked, operation.Remaining.TotalMilliseconds);
                    lastState = state;
                }
                if (observed.FrameId > lastFrame)
                {
                    lastFrame = observed.FrameId;
                    stable = clicked && observed.MapReady && !observed.SelectorOpen ? stable + 1 : 0;
                    if (stable >= 2) return true;
                    // 先承接前次点击的迟到结果，只有新帧仍显示选择器和候选时才允许再次点击。
                    if (observed.SelectorOpen && observed.HasCandidate && attempts < 3
                        && (!clicked || operation.Elapsed - lastClickAt >= TimeSpan.FromMilliseconds(1500)))
                    {
                        var applied = await clickFreshCandidate(++attempts, operation.Token);
                        operation.Check();
                        clicked |= applied;
                        lastClickAt = operation.Elapsed;
                        logger?.LogDebug("AREA_SELECTION_CLICK op={Op} attempt={Attempt} applied={Applied}",
                            operation.Id, attempts, applied);
                    }
                }
                await delay(100, operation.Token);
            }
        }, logger, clock);

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
