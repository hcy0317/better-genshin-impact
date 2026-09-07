using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class NativeCombatSwitchTests
{
    [Fact]
    public void NativeSwitchWaitsThroughOneSecondCooldownAndConfirmsTwoFrames()
    {
        var elapsed = 0;
        var confirmed = AvatarSwitchConfirmationPolicy.TryConfirm(2,
            NativeCombatFlowRunner.SwitchAttempts,
            () => elapsed >= 1000 ? 2 : 1,
            _ => { }, milliseconds => elapsed += milliseconds, default);
        Assert.True(confirmed);
        Assert.Equal(1100, elapsed);
    }

    [Fact]
    public void LastFrameAloneOrUnknownActorCannotConfirmSwitch()
    {
        var frames = new Queue<int>([1, -1, 2]);
        Assert.False(AvatarSwitchConfirmationPolicy.TryConfirm(2, 3,
            () => frames.Dequeue(), _ => { }, _ => { }, default));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.False(AvatarSwitchConfirmationPolicy.TryConfirm(2, 10,
            () => throw new Exception("取消后不能截图"), _ => throw new Exception("取消后不能输入"),
            _ => { }, cancelled.Token));
    }

    [Fact]
    public void CancellationDuringObservationCannotSendTheNextSwitchKey()
    {
        using var cts = new CancellationTokenSource();
        var sentInput = false;
        Assert.False(AvatarSwitchConfirmationPolicy.TryConfirm(2, 10,
            () => { cts.Cancel(); return 1; }, _ => sentInput = true, _ => { }, cts.Token));
        Assert.False(sentInput);
    }

    [Fact]
    public async Task OnlyRawAtomicContinuationsCanReuseAConfirmedActor()
    {
        var game = new AdmissionGame();
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            call(宏)
            琴 wait(0.1)
            segment(宏,define,atomic) {
                琴 keydown(VK_LBUTTON),wait(0.1),moveby(2,0),keyup(VK_LBUTTON)
                琴 e(feed=芙宁娜)
            }
            """), game, new FakeTimeProvider());
        await execution.RunRoundAsync();
        Assert.Equal(new[] { false, true, true, true, false, false, false }, game.Reuse);
    }

    private sealed class AdmissionGame : ICombatFlowGame
    {
        public List<bool> Reuse { get; } = [];
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            Reuse.Add(action.CanReuseConfirmedActor);
            return ValueTask.FromResult(action.TryBeginInput() ? CombatFlowResult.Succeeded : CombatFlowResult.Skipped);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
