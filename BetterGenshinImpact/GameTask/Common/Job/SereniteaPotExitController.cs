using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal enum SereniteaPotExitState
{
    MainUi,
    TalkUi,
    TalkOptionsUi,
    ClosableUi,
    OtherUi,
}

internal sealed class SereniteaPotExitException(string message) : InvalidOperationException(message);

internal static class SereniteaPotExitController
{
    internal static Task<bool> ExitToMainUiAsync(
        Func<SereniteaPotExitState> observeState,
        Func<CancellationToken, Task<bool>> trySelectGoodbye,
        Action advanceDialogue,
        Action closeKnownInterface,
        Action pressEscape,
        Func<int, CancellationToken, Task> delay,
        CancellationToken ct,
        int maxCycles = 3)
    {
        return ExitToMainUiAsync(
            observeState,
            trySelectGoodbye,
            _ => Task.FromResult(false),
            advanceDialogue,
            closeKnownInterface,
            pressEscape,
            delay,
            ct,
            maxCycles);
    }

    internal static async Task<bool> ExitToMainUiAsync(
        Func<SereniteaPotExitState> observeState,
        Func<CancellationToken, Task<bool>> trySelectGoodbye,
        Func<CancellationToken, Task<bool>> trySelectLastOption,
        Action advanceDialogue,
        Action closeKnownInterface,
        Action pressEscape,
        Func<int, CancellationToken, Task> delay,
        CancellationToken ct,
        int maxCycles = 3)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCycles, 1);

        for (var cycle = 0; cycle < maxCycles; cycle++)
        {
            ct.ThrowIfCancellationRequested();
            if (ObserveAndCheck(observeState, ct) == SereniteaPotExitState.MainUi)
            {
                return true;
            }

            var conversationExited = await ExitConversationAsync(
                observeState,
                trySelectGoodbye,
                trySelectLastOption,
                advanceDialogue,
                delay,
                ct);
            if (!conversationExited)
            {
                return false;
            }

            if (await ExitInterfaceAsync(
                    observeState,
                    closeKnownInterface,
                    pressEscape,
                    delay,
                    ct))
            {
                return true;
            }
        }

        ct.ThrowIfCancellationRequested();
        return ObserveAndCheck(observeState, ct) == SereniteaPotExitState.MainUi;
    }

    internal static Task<bool> ExitConversationAsync(
        Func<SereniteaPotExitState> observeState,
        Func<CancellationToken, Task<bool>> trySelectGoodbye,
        Action advanceDialogue,
        Func<int, CancellationToken, Task> delay,
        CancellationToken ct,
        int maxAttempts = 30,
        int retryDelayMs = 500)
    {
        return ExitConversationAsync(
            observeState,
            trySelectGoodbye,
            _ => Task.FromResult(false),
            advanceDialogue,
            delay,
            ct,
            maxAttempts,
            retryDelayMs);
    }

    internal static async Task<bool> ExitConversationAsync(
        Func<SereniteaPotExitState> observeState,
        Func<CancellationToken, Task<bool>> trySelectGoodbye,
        Func<CancellationToken, Task<bool>> trySelectLastOption,
        Action advanceDialogue,
        Func<int, CancellationToken, Task> delay,
        CancellationToken ct,
        int maxAttempts = 30,
        int retryDelayMs = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        var dialogueAttempts = 0;
        var optionAttempts = 0;
        while (dialogueAttempts < maxAttempts || optionAttempts < maxAttempts)
        {
            ct.ThrowIfCancellationRequested();
            var state = ObserveAndCheck(observeState, ct);
            if (state is SereniteaPotExitState.MainUi or SereniteaPotExitState.ClosableUi)
            {
                return true;
            }
            if (!IsConversationState(state))
            {
                if (dialogueAttempts < maxAttempts) dialogueAttempts++;
                else optionAttempts++;
                await delay(retryDelayMs, ct);
                continue;
            }

            if (state == SereniteaPotExitState.TalkUi)
            {
                if (dialogueAttempts >= maxAttempts)
                {
                    return false;
                }

                dialogueAttempts++;
                advanceDialogue();
                await delay(retryDelayMs, ct);
                continue;
            }

            if (optionAttempts >= maxAttempts)
            {
                return false;
            }

            optionAttempts++;
            var selectedGoodbye = await trySelectGoodbye(ct);
            await delay(retryDelayMs, ct);
            state = ObserveAndCheck(observeState, ct);
            if (state is SereniteaPotExitState.MainUi or SereniteaPotExitState.ClosableUi)
            {
                return true;
            }

            if (!IsConversationState(state) || state == SereniteaPotExitState.TalkUi || selectedGoodbye)
            {
                continue;
            }

            if (!await trySelectLastOption(ct))
            {
                advanceDialogue();
            }
            await delay(retryDelayMs, ct);
        }

        ct.ThrowIfCancellationRequested();
        return ObserveAndCheck(observeState, ct) is SereniteaPotExitState.MainUi or SereniteaPotExitState.ClosableUi;
    }

    internal static async Task<bool> ExitInterfaceAsync(
        Func<SereniteaPotExitState> observeState,
        Action closeKnownInterface,
        Action pressEscape,
        Func<int, CancellationToken, Task> delay,
        CancellationToken ct,
        int maxAttempts = 8,
        int retryDelayMs = 500)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxAttempts, 1);

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var state = ObserveAndCheck(observeState, ct);
            if (state == SereniteaPotExitState.MainUi)
            {
                return true;
            }
            if (IsConversationState(state))
            {
                return false;
            }

            if (state != SereniteaPotExitState.ClosableUi)
            {
                await delay(retryDelayMs, ct);
                continue;
            }

            closeKnownInterface();
            await delay(retryDelayMs, ct);
        }

        ct.ThrowIfCancellationRequested();
        return ObserveAndCheck(observeState, ct) == SereniteaPotExitState.MainUi;
    }

    private static SereniteaPotExitState ObserveAndCheck(Func<SereniteaPotExitState> observeState, CancellationToken ct)
    {
        var state = observeState();
        ct.ThrowIfCancellationRequested();
        return state;
    }

    private static bool IsConversationState(SereniteaPotExitState state)
    {
        return state is SereniteaPotExitState.TalkUi or SereniteaPotExitState.TalkOptionsUi;
    }
}
