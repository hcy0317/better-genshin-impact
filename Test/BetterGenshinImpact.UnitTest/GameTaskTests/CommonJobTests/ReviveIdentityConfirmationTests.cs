using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ReviveIdentityConfirmationTests
{
    private static readonly ReviveTarget Target = new("钟离", 1);
    private static readonly IReadOnlyDictionary<int, string> Party = new Dictionary<int, string> { [1] = "钟离", [2] = "娜维娅" };

    [Fact]
    public async Task AnotherLivingActorCannotProveTheKnownDefeatedActorRecovered()
    {
        long frame = 0;
        var party = new Dictionary<int, string> { [1] = "钟离", [2] = "娜维娅" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask,
            () => new(++frame, true, ReviveUiState.None, 2, party),
            new ReviveTarget("钟离", 1), () => Task.CompletedTask, default));
    }

    [Fact]
    public async Task SameNamedActorInTheSameSlotNeedsTwoFreshSuccessfulFrames()
    {
        long frame = 0;
        await Assert.ThrowsAsync<CombatRecoveryCompletedException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask, () => new(++frame, true, ReviveUiState.None, 1, Party),
            Target, () => Task.CompletedTask, default));
        Assert.Equal(2, frame);
    }

    [Theory]
    [InlineData("same-frame")]
    [InlineData("mapping-changed")]
    [InlineData("missing-target")]
    [InlineData("revive")]
    [InlineData("unknown")]
    public async Task StaleOrChangedRecoveryEvidenceCannotAuthorizeTheRoute(string failure)
    {
        var calls = 0;
        ReviveRecoveryFrame Capture()
        {
            var sample = new ReviveRecoveryFrame(++calls, true, ReviveUiState.None, 1, Party);
            if (calls == 1) return sample;
            return failure switch
            {
                "same-frame" => sample with { FrameId = 1 },
                "mapping-changed" => sample with { Party = new Dictionary<int, string> { [1] = "娜维娅", [2] = "钟离" } },
                "missing-target" => sample with { Party = new Dictionary<int, string>() },
                "revive" => sample with { Revive = ReviveUiState.FoodPrompt },
                _ => sample with { FrameId = 0, MainReady = false }
            };
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask, Capture, Target, () => Task.CompletedTask, default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RealSwitchObservationOrderDoesNotBindAPreexistingRevivePrompt(bool preexistingPrompt)
    {
        var inputs = 0;
        long frame = 0;
        ReviveRecoveryFrame? before = null;
        ReviveTarget? bound = null;
        Assert.Throws<ReviveObserved>(() => AvatarSwitchConfirmationPolicy.TryConfirm(1, 4, () =>
        {
            var sample = new ReviveRecoveryFrame(++frame, true,
                preexistingPrompt || inputs > 0 ? ReviveUiState.FoodPrompt : ReviveUiState.None, 2, Party);
            if (sample.Revive != ReviveUiState.None)
            {
                bound = ReviveTarget.FromSelection(Target, inputs > 0, before, sample);
                throw new ReviveObserved();
            }
            before = sample;
            return sample.ActiveIndex;
        }, _ => inputs++, _ => { }, default));
        Assert.Equal(preexistingPrompt ? 0 : 1, inputs);
        Assert.Equal(preexistingPrompt ? null : Target, bound);
    }

    [Fact]
    public void SelectionBindingRejectsChangedIdentityBadFramesAndUnsubmittedInput()
    {
        var before = new ReviveRecoveryFrame(1, true, ReviveUiState.None, 2, Party);
        var after = new ReviveRecoveryFrame(2, true, ReviveUiState.FoodPrompt, 2, Party);
        Assert.Null(ReviveTarget.FromSelection(Target, false, before, after));
        Assert.Null(ReviveTarget.FromSelection(Target, true, before with { FrameId = 0 }, after));
        Assert.Null(ReviveTarget.FromSelection(Target, true, before, after with { FrameId = 1 }));
        Assert.Null(ReviveTarget.FromSelection(Target, true, before, after with { Party = new Dictionary<int, string> { [1] = "娜维娅" } }));
        Assert.Null(ReviveTarget.FromSelection(Target, true, before, after with { Revive = ReviveUiState.FullPartyDefeat }));
    }

    [Fact]
    public async Task CancellationAndCaptureFailureNeverBecomeRecoveryCompletion()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var ran = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => { ran = true; return Task.CompletedTask; }, () => new(1, true, ReviveUiState.None, 1, Party),
            Target, () => Task.CompletedTask, cancellation.Token));
        Assert.False(ran);
        await Assert.ThrowsAsync<IOException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask, () => throw new IOException("capture failed"), Target, () => Task.CompletedTask, default));
    }

    private sealed class ReviveObserved : Exception;
}
