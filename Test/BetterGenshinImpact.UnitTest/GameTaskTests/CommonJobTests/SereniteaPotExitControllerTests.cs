using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class SereniteaPotExitControllerTests
{
    [Theory]
    [InlineData("initial-main")]
    [InlineData("conversation-main")]
    [InlineData("conversation-closable")]
    [InlineData("after-goodbye-main")]
    [InlineData("after-goodbye-closable")]
    [InlineData("conversation-final-main")]
    [InlineData("conversation-final-closable")]
    [InlineData("interface-main")]
    [InlineData("retry-cycle-main")]
    [InlineData("whole-final-main")]
    [InlineData("conversation-talk")]
    [InlineData("conversation-options")]
    [InlineData("interface-closable")]
    [InlineData("after-goodbye-options")]
    [InlineData("unknown-observe")]
    public async Task ExitToMainUiChecksCancellationAfterEveryObservationResult(string boundary)
    {
        using var cts = new CancellationTokenSource();
        var states = boundary switch
        {
            "initial-main" => new[] { SereniteaPotExitState.MainUi },
            "conversation-main" => new[] { SereniteaPotExitState.OtherUi, SereniteaPotExitState.MainUi },
            "conversation-closable" => new[] { SereniteaPotExitState.OtherUi, SereniteaPotExitState.ClosableUi },
            "after-goodbye-main" => new[] { SereniteaPotExitState.TalkOptionsUi, SereniteaPotExitState.TalkOptionsUi, SereniteaPotExitState.MainUi },
            "after-goodbye-closable" => new[] { SereniteaPotExitState.TalkOptionsUi, SereniteaPotExitState.TalkOptionsUi, SereniteaPotExitState.ClosableUi },
            "conversation-final-main" => Enumerable.Repeat(SereniteaPotExitState.OtherUi, 61).Append(SereniteaPotExitState.MainUi).ToArray(),
            "conversation-final-closable" => Enumerable.Repeat(SereniteaPotExitState.OtherUi, 61).Append(SereniteaPotExitState.ClosableUi).ToArray(),
            "interface-main" => new[] { SereniteaPotExitState.ClosableUi, SereniteaPotExitState.ClosableUi, SereniteaPotExitState.MainUi },
            "retry-cycle-main" => new[] { SereniteaPotExitState.ClosableUi, SereniteaPotExitState.ClosableUi, SereniteaPotExitState.TalkUi, SereniteaPotExitState.MainUi },
            "whole-final-main" => Enumerable.Range(0, 3).SelectMany(_ => new[]
                { SereniteaPotExitState.ClosableUi, SereniteaPotExitState.ClosableUi, SereniteaPotExitState.TalkUi })
                .Append(SereniteaPotExitState.MainUi).ToArray(),
            "conversation-talk" => new[] { SereniteaPotExitState.OtherUi, SereniteaPotExitState.TalkUi },
            "conversation-options" => new[] { SereniteaPotExitState.OtherUi, SereniteaPotExitState.TalkOptionsUi },
            "interface-closable" => new[] { SereniteaPotExitState.ClosableUi, SereniteaPotExitState.ClosableUi, SereniteaPotExitState.ClosableUi },
            "after-goodbye-options" => new[] { SereniteaPotExitState.TalkOptionsUi, SereniteaPotExitState.TalkOptionsUi, SereniteaPotExitState.TalkOptionsUi },
            _ => new[] { SereniteaPotExitState.OtherUi, SereniteaPotExitState.OtherUi }
        };
        var observations = 0;
        SereniteaPotExitState Observe()
        {
            Assert.True(observations < states.Length, "No observation may run after the cancelling observation");
            var state = states[observations++];
            if (observations == states.Length) cts.Cancel();
            return state;
        }
        void InputAllowed() => Assert.False(cts.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SereniteaPotExitController.ExitToMainUiAsync(
            Observe,
            _ => { InputAllowed(); return Task.FromResult(false); },
            _ => { InputAllowed(); return Task.FromResult(false); },
            InputAllowed, InputAllowed, InputAllowed,
            (ms, _) => { InputAllowed(); Assert.Equal(500, ms); return Task.CompletedTask; }, cts.Token));
        Assert.Equal(states.Length, observations);
    }

    [Fact]
    public async Task ExitToMainUiKeepsDefaultObservationCountWhenEleventhObserveIsNotCancelled()
    {
        var observations = 0;
        var closes = 0;
        var waits = 0;
        var otherInputs = 0;
        var exited = await SereniteaPotExitController.ExitToMainUiAsync(
            () => ++observations <= 10 ? SereniteaPotExitState.ClosableUi : SereniteaPotExitState.MainUi,
            _ => { otherInputs++; return Task.FromResult(false); },
            () => otherInputs++, () => closes++, () => otherInputs++,
            (ms, _) => { Assert.Equal(500, ms); waits++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(exited);
        Assert.Equal(11, observations);
        Assert.Equal(8, closes);
        Assert.Equal(8, waits);
        Assert.Equal(0, otherInputs);
    }

    [Fact]
    public async Task ExitToMainUiCannotPublishMainWhenEleventhObserveCancels()
    {
        using var cts = new CancellationTokenSource();
        var observations = 0;
        var closes = 0;
        var waits = 0;
        var otherInputs = 0;
        SereniteaPotExitState Observe()
        {
            observations++;
            if (observations <= 10) return SereniteaPotExitState.ClosableUi;
            cts.Cancel();
            return SereniteaPotExitState.MainUi;
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SereniteaPotExitController.ExitToMainUiAsync(
            Observe, _ => { otherInputs++; return Task.FromResult(false); },
            () => otherInputs++, () => closes++, () => otherInputs++,
            (ms, _) =>
            {
                Assert.False(cts.IsCancellationRequested);
                Assert.Equal(500, ms);
                waits++;
                return Task.CompletedTask;
            }, cts.Token));
        Assert.Equal(11, observations);
        Assert.Equal(8, closes);
        Assert.Equal(8, waits);
        Assert.Equal(0, otherInputs);
    }

    [Fact]
    public async Task ExitToMainUiAllUnknownConsumesOnlyExistingObservationsWithoutInput()
    {
        var inputs = 0;
        var waits = new List<int>();
        var exited = await SereniteaPotExitController.ExitToMainUiAsync(
            () => SereniteaPotExitState.OtherUi,
            _ => { inputs++; return Task.FromResult(false); },
            _ => { inputs++; return Task.FromResult(false); },
            () => inputs++, () => inputs++, () => inputs++,
            (ms, _) => { waits.Add(ms); return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(exited);
        Assert.Equal(0, inputs);
        Assert.Equal(60, waits.Count);
        Assert.All(waits, ms => Assert.Equal(500, ms));
    }

    [Fact]
    public async Task ExitToMainUiKeepsRemainingOptionBudgetAfterUnknownConsumesDialogueObservations()
    {
        var state = SereniteaPotExitState.OtherUi;
        var waits = 0;
        var goodbyes = 0;
        var otherInputs = 0;
        var exited = await SereniteaPotExitController.ExitToMainUiAsync(
            () => state,
            _ => { goodbyes++; state = SereniteaPotExitState.MainUi; return Task.FromResult(true); },
            _ => { otherInputs++; return Task.FromResult(false); },
            () => otherInputs++, () => otherInputs++, () => otherInputs++,
            (ms, _) =>
            {
                Assert.Equal(500, ms);
                waits++;
                if (waits == 30) state = SereniteaPotExitState.TalkOptionsUi;
                return Task.CompletedTask;
            }, CancellationToken.None);
        Assert.True(exited);
        Assert.Equal(1, goodbyes);
        Assert.Equal(0, otherInputs);
        Assert.Equal(31, waits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExitToMainUiDoesNotResetOptionBudgetOrAcceptUnverifiedGoodbye(bool submitted)
    {
        var afterChoice = false;
        var goodbyes = 0;
        var otherInputs = 0;
        var waits = 0;
        SereniteaPotExitState Observe()
        {
            if (!submitted && afterChoice) { afterChoice = false; return SereniteaPotExitState.OtherUi; }
            return SereniteaPotExitState.TalkOptionsUi;
        }
        var exited = await SereniteaPotExitController.ExitToMainUiAsync(Observe,
            _ => { goodbyes++; afterChoice = true; return Task.FromResult(submitted); },
            _ => { otherInputs++; return Task.FromResult(false); },
            () => otherInputs++, () => otherInputs++, () => otherInputs++,
            (ms, _) => { Assert.Equal(500, ms); waits++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(exited);
        Assert.Equal(30, goodbyes);
        Assert.Equal(0, otherInputs);
        Assert.Equal(30, waits);
    }

    [Fact]
    public async Task ExitToMainUiDoesNotAuthorizeMoreInputWhenKnownCloseBecomesUnknown()
    {
        var state = SereniteaPotExitState.ClosableUi;
        var closes = 0;
        var otherInputs = 0;
        var exited = await SereniteaPotExitController.ExitToMainUiAsync(
            () => state,
            _ => { otherInputs++; return Task.FromResult(false); },
            _ => { otherInputs++; return Task.FromResult(false); },
            () => otherInputs++,
            () => { closes++; state = SereniteaPotExitState.OtherUi; },
            () => otherInputs++,
            (ms, _) => { Assert.Equal(500, ms); return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(exited);
        Assert.Equal(1, closes);
        Assert.Equal(0, otherInputs);
    }

    [Fact]
    public async Task ExitToMainUiKnownInterfaceCannotSucceedWithoutMainPostcondition()
    {
        var closes = 0;
        var otherInputs = 0;
        var waits = 0;
        var exited = await SereniteaPotExitController.ExitToMainUiAsync(
            () => SereniteaPotExitState.ClosableUi,
            _ => { otherInputs++; return Task.FromResult(false); },
            _ => { otherInputs++; return Task.FromResult(false); },
            () => otherInputs++, () => closes++, () => otherInputs++,
            (ms, _) => { Assert.Equal(500, ms); waits++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(exited);
        Assert.Equal(24, closes);
        Assert.Equal(24, waits);
        Assert.Equal(0, otherInputs);
    }

    [Fact]
    public async Task ExitToMainUiCancellationDuringUnknownObservationStopsWithoutInput()
    {
        using var cts = new CancellationTokenSource();
        var inputs = 0;
        var waits = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SereniteaPotExitController.ExitToMainUiAsync(
            () => SereniteaPotExitState.OtherUi,
            _ => { inputs++; return Task.FromResult(false); },
            _ => { inputs++; return Task.FromResult(false); },
            () => inputs++, () => inputs++, () => inputs++,
            (ms, _) => { Assert.Equal(500, ms); waits++; cts.Cancel(); return Task.CompletedTask; }, cts.Token));
        Assert.Equal(0, inputs);
        Assert.Equal(1, waits);
    }

    [Fact]
    public async Task ExitToMainUiCannotPublishMainAfterLastInterfaceWaitWasCancelled()
    {
        using var cts = new CancellationTokenSource();
        var state = SereniteaPotExitState.ClosableUi;
        var closes = 0;
        var waits = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SereniteaPotExitController.ExitToMainUiAsync(
            () => state,
            _ => throw new Exception("Known interface must not select dialogue"),
            () => throw new Exception("Known interface must not advance dialogue"),
            () => closes++,
            () => throw new Exception("No new Escape is permitted"),
            (ms, _) =>
            {
                Assert.Equal(500, ms);
                waits++;
                if (waits == 8) { state = SereniteaPotExitState.MainUi; cts.Cancel(); }
                return Task.CompletedTask;
            }, cts.Token));
        Assert.Equal(8, closes);
        Assert.Equal(8, waits);
    }

    [Fact]
    public async Task ExitToMainUiKeepsOriginalConversationOpportunitiesAcrossTransientUnknown()
    {
        var afterChoice = false;
        var main = false;
        var goodbyes = 0;
        var otherInputs = 0;
        var waits = new List<int>();
        SereniteaPotExitState Observe()
        {
            if (main) return SereniteaPotExitState.MainUi;
            if (afterChoice) { afterChoice = false; return SereniteaPotExitState.OtherUi; }
            return SereniteaPotExitState.TalkOptionsUi;
        }
        var exited = await SereniteaPotExitController.ExitToMainUiAsync(Observe, _ =>
        {
            goodbyes++;
            if (goodbyes == 4) { main = true; return Task.FromResult(true); }
            afterChoice = true;
            return Task.FromResult(false);
        }, _ => { otherInputs++; return Task.FromResult(false); },
            () => otherInputs++, () => otherInputs++, () => otherInputs++,
            (ms, _) => { waits.Add(ms); return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(exited);
        Assert.Equal(4, goodbyes);
        Assert.Equal(0, otherInputs);
        Assert.Equal(new[] { 500, 500, 500, 500 }, waits.ToArray());
    }

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
    public async Task ExitConversationAsync_ShouldRetryGoodbyeUntilKnownExitAppears()
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
                    state = SereniteaPotExitState.MainUi;
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
                state = SereniteaPotExitState.ClosableUi;
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
    public async Task ExitInterfaceAsync_UnknownDoesNotAuthorizeEscapeOrSuccess()
    {
        var escapeAttempts = 0;
        var waits = new List<int>();
        var exited = await SereniteaPotExitController.ExitInterfaceAsync(
            () => SereniteaPotExitState.OtherUi,
            () => throw new Exception("Unknown cannot authorize close"),
            () => escapeAttempts++,
            (ms, _) => { waits.Add(ms); return Task.CompletedTask; },
            CancellationToken.None, maxAttempts: 3);
        Assert.False(exited);
        Assert.Equal(0, escapeAttempts);
        Assert.Equal(new[] { 500, 500, 500 }, waits.ToArray());
    }

    [Fact]
    public async Task ExitInterfaceAsync_ShouldObserveMainAfterUnknownWithoutPressingEscape()
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
