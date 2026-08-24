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
            if (observeState() == SereniteaPotExitState.MainUi)
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

        return observeState() == SereniteaPotExitState.MainUi;
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
            var state = observeState();
            if (!IsConversationState(state))
            {
                return true;
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
            state = observeState();
            if (!IsConversationState(state))
            {
                return true;
            }

            if (state == SereniteaPotExitState.TalkUi || selectedGoodbye)
            {
                continue;
            }

            if (!await trySelectLastOption(ct))
            {
                advanceDialogue();
            }
            await delay(retryDelayMs, ct);
        }

        return !IsConversationState(observeState());
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
            var state = observeState();
            if (state == SereniteaPotExitState.MainUi)
            {
                return true;
            }
            if (IsConversationState(state))
            {
                return false;
            }

            if (state == SereniteaPotExitState.OtherUi)
            {
                await delay(retryDelayMs, ct);
                state = observeState();
                if (state == SereniteaPotExitState.MainUi)
                {
                    return true;
                }
                if (IsConversationState(state))
                {
                    return false;
                }
            }

            if (state == SereniteaPotExitState.ClosableUi)
            {
                closeKnownInterface();
            }
            else
            {
                pressEscape();
            }
            await delay(retryDelayMs, ct);
        }

        return observeState() == SereniteaPotExitState.MainUi;
    }

    private static bool IsConversationState(SereniteaPotExitState state)
    {
        return state is SereniteaPotExitState.TalkUi or SereniteaPotExitState.TalkOptionsUi;
    }
}
