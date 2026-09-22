using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatRecoveryNativeReceiptTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void OnlyCompleteNativeOriginalAllowsOneRecoveryAndPartialRecoveryCannotRepeat(int originalSubmitted)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var context = new CombatFlowContext(clock);
        using var attempts = new CombatSkillAttempts(context.BattleId);
        var command = new CombatCommand("班尼特", "q(required)");
        var action = new CombatFlowAction(command, context, () => true, 8);
        var native = new CombatNativeInput(clock, NullLogger.Instance, () => { });
        var submits = 0;
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            return (uint)(++submits == 1 ? originalSubmitted : 1);
        }, () => 5);
        void SendOriginal() => CombatSkillInput.Send(attempts, action, "班尼特", source.Next(),
            (request, begin) => native.Submit(request, "q", () => dispatcher.DispatchInput(new User32.INPUT[2]), default, begin), default, clock);
        if (originalSubmitted == 0)
        {
            Assert.Throws<InputDispatchException>(SendOriginal);
            Assert.Null(action.PendingAttempt);
            Assert.Equal(1, submits);
            return;
        }
        SendOriginal();
        var confirmation = new CombatFlowAction(command, context, () => true, 8, confirmationAttempt: action.PendingAttempt);
        CombatSkillObservation Ready()
        {
            clock.Advance(TimeSpan.FromMilliseconds(210));
            return new CombatSkillObservation(context.BattleId, 0, context.Now, false, true).WithSource(source.Next(), clock);
        }
        Assert.Null(attempts.TryClaimRecovery(confirmation, Ready(), true));
        var pulse = attempts.TryClaimRecovery(confirmation, Ready(), true);
        if (originalSubmitted < 2) { Assert.Null(pulse); Assert.Equal(1, submits); return; }
        Assert.NotNull(pulse);
        CombatSkillInput.SendRecovery(attempts, confirmation, pulse!,
            (request, begin) => native.Submit(request, "recovery-q", () => dispatcher.DispatchInput(new User32.INPUT[2]), default, begin), default, clock);
        Assert.Equal(2, submits);
        Assert.Null(attempts.TryClaimRecovery(confirmation, Ready(), true));
        Assert.Null(attempts.TryClaimRecovery(confirmation, Ready(), true));
    }
}
