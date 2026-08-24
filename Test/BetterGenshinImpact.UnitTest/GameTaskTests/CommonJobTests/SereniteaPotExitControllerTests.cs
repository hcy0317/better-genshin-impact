using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class SereniteaPotExitControllerTests
{
    [Fact]
    public async Task ExitConversationAsync_ShouldAdvanceDialogueBeforeLookingForGoodbye()
    {
        var state = SereniteaPotExitState.TalkUi;
        var selectAttempts = 0;
        var advanceAttempts = 0;

        var exited = await SereniteaPotExitController.ExitConversationAsync(
            () => state,
            _ =>
            {
                selectAttempts++;
                state = SereniteaPotExitState.MainUi;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(false),
            () =>
            {
                advanceAttempts++;
                state = SereniteaPotExitState.TalkOptionsUi;
            },
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            maxAttempts: 2);

        Assert.True(exited);
        Assert.Equal(1, advanceAttempts);
        Assert.Equal(1, selectAttempts);
    }

    [Fact]
    public async Task ExitConversationAsync_ShouldAllowSeveralDialoguePagesBeforeOptionsAppear()
    {
        var state = SereniteaPotExitState.TalkUi;
        var advanceAttempts = 0;

        var exited = await SereniteaPotExitController.ExitConversationAsync(
            () => state,
            _ =>
            {
                state = SereniteaPotExitState.MainUi;
                return Task.FromResult(true);
            },
            _ => Task.FromResult(false),
            () =>
            {
                advanceAttempts++;
                if (advanceAttempts == 5)
                {
                    state = SereniteaPotExitState.TalkOptionsUi;
                }
            },
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.True(exited);
        Assert.Equal(5, advanceAttempts);
    }

    [Fact]
    public async Task ExitConversationAsync_ShouldRetryGoodbyeUntilTalkUiDisappears()
    {
        var state = SereniteaPotExitState.TalkOptionsUi;
        var selectAttempts = 0;
        var advanceAttempts = 0;

        var exited = await SereniteaPotExitController.ExitConversationAsync(
            () => state,
            _ =>
            {
                selectAttempts++;
                if (selectAttempts == 2)
                {
                    state = SereniteaPotExitState.OtherUi;
                }
                return Task.FromResult(true);
            },
            () => advanceAttempts++,
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            maxAttempts: 3);

        Assert.True(exited);
        Assert.Equal(2, selectAttempts);
        Assert.Equal(0, advanceAttempts);
    }

    [Fact]
    public async Task ExitConversationAsync_ShouldUseLastOptionWhenGoodbyeOcrFails()
    {
        var state = SereniteaPotExitState.TalkOptionsUi;
        var lastOptionAttempts = 0;
        var advanceAttempts = 0;

        var exited = await SereniteaPotExitController.ExitConversationAsync(
            () => state,
            _ => Task.FromResult(false),
            _ =>
            {
                lastOptionAttempts++;
                state = SereniteaPotExitState.OtherUi;
                return Task.FromResult(true);
            },
            () => advanceAttempts++,
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            maxAttempts: 1);

        Assert.True(exited);
        Assert.Equal(1, lastOptionAttempts);
        Assert.Equal(0, advanceAttempts);
    }

    [Fact]
    public async Task ExitConversationAsync_ShouldAllowFarewellDialogueAfterSelectingLastOption()
    {
        var state = SereniteaPotExitState.TalkOptionsUi;
        var advanceAttempts = 0;

        var exited = await SereniteaPotExitController.ExitConversationAsync(
            () => state,
            _ => Task.FromResult(false),
            _ =>
            {
                state = SereniteaPotExitState.TalkUi;
                return Task.FromResult(true);
            },
            () =>
            {
                advanceAttempts++;
                if (advanceAttempts == 15)
                {
                    state = SereniteaPotExitState.MainUi;
                }
            },
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.True(exited);
        Assert.Equal(15, advanceAttempts);
    }

    [Fact]
    public async Task ExitInterfaceAsync_ShouldCloseKnownUiUntilMainUiAppears()
    {
        var state = SereniteaPotExitState.ClosableUi;
        var closeAttempts = 0;
        var escapeAttempts = 0;

        var exited = await SereniteaPotExitController.ExitInterfaceAsync(
            () => state,
            () =>
            {
                closeAttempts++;
                if (closeAttempts == 2)
                {
                    state = SereniteaPotExitState.MainUi;
                }
            },
            () => escapeAttempts++,
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            maxAttempts: 3);

        Assert.True(exited);
        Assert.Equal(2, closeAttempts);
        Assert.Equal(0, escapeAttempts);
    }

    [Fact]
    public async Task ExitInterfaceAsync_ShouldPressEscapeForUnknownUiUntilMainUiAppears()
    {
        var state = SereniteaPotExitState.OtherUi;
        var escapeAttempts = 0;

        var exited = await SereniteaPotExitController.ExitInterfaceAsync(
            () => state,
            () => { },
            () =>
            {
                escapeAttempts++;
                if (escapeAttempts == 2)
                {
                    state = SereniteaPotExitState.MainUi;
                }
            },
            (_, _) => Task.CompletedTask,
            CancellationToken.None,
            maxAttempts: 3);

        Assert.True(exited);
        Assert.Equal(2, escapeAttempts);
    }

    [Fact]
    public async Task ExitInterfaceAsync_ShouldWaitForUnknownUiTransitionBeforePressingEscape()
    {
        var state = SereniteaPotExitState.OtherUi;
        var escapeAttempts = 0;

        var exited = await SereniteaPotExitController.ExitInterfaceAsync(
            () => state,
            () => { },
            () => escapeAttempts++,
            (_, _) =>
            {
                state = SereniteaPotExitState.MainUi;
                return Task.CompletedTask;
            },
            CancellationToken.None,
            maxAttempts: 1);

        Assert.True(exited);
        Assert.Equal(0, escapeAttempts);
    }

    [Fact]
    public async Task ExitToMainUiAsync_ShouldHandleClosableUiThatRevealsTalkUi()
    {
        var state = SereniteaPotExitState.ClosableUi;
        var selectAttempts = 0;
        var advanceAttempts = 0;
        var closeAttempts = 0;

        var exited = await SereniteaPotExitController.ExitToMainUiAsync(
            () => state,
            _ =>
            {
                selectAttempts++;
                state = SereniteaPotExitState.MainUi;
                return Task.FromResult(true);
            },
            () =>
            {
                advanceAttempts++;
                state = SereniteaPotExitState.MainUi;
            },
            () =>
            {
                closeAttempts++;
                state = SereniteaPotExitState.TalkOptionsUi;
            },
            () => state = SereniteaPotExitState.MainUi,
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.True(exited);
        Assert.Equal(1, closeAttempts);
        Assert.Equal(1, selectAttempts);
        Assert.Equal(0, advanceAttempts);
    }

    [Fact]
    public async Task ExitToMainUiAsync_ShouldFailWhenTalkUiNeverDisappears()
    {
        var exited = await SereniteaPotExitController.ExitToMainUiAsync(
            () => SereniteaPotExitState.TalkUi,
            _ => Task.FromResult(false),
            () => { },
            () => { },
            () => { },
            (_, _) => Task.CompletedTask,
            CancellationToken.None);

        Assert.False(exited);
    }

    [Fact]
    public async Task ExitToMainUiAsync_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SereniteaPotExitController.ExitToMainUiAsync(
                () => SereniteaPotExitState.TalkUi,
                _ => Task.FromResult(false),
                () => { },
                () => { },
                () => { },
                (_, _) => Task.CompletedTask,
                cts.Token));
    }
}
