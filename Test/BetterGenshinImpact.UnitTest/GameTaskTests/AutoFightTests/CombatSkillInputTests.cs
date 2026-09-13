using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatSkillInputTests
{
    [Fact]
    public void SendingInputYieldsPendingAndOnlyANewCooldownFrameCanConfirmIt()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        using var context = new CombatFlowContext(clock);
        using var attempts = new CombatSkillAttempts(context.BattleId);
        var command = new CombatCommand("琴", "e(required)");
        var action = new CombatFlowAction(command, context, () => true, 8);
        var sent = 0;
        Assert.Equal(CombatFlowResult.Pending, CombatSkillInput.Send(attempts, action, "琴", producer.Next(),
            () => { sent++; clock.Advance(TimeSpan.FromMilliseconds(100)); }, default, clock));
        var next = new CombatFlowAction(command, context, () => true, 8);
        Assert.Equal(CombatFlowResult.Pending, CombatSkillInput.Send(attempts, next, "琴", producer.Next(), () => sent++, default, clock));
        Assert.Equal(1, sent);
        Assert.Null(attempts.TakeConfirmation("琴", Method.Skill, commandId: action.CommandId, now: context.Now));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.True(attempts.Observe("琴", Method.Skill,
            new CombatSkillObservation(context.BattleId, 0, context.Now, true, false).WithSource(producer.Next(), clock)));
        var receipt = attempts.TakeConfirmation("琴", Method.Skill, action.CommandId, context.Now);
        Assert.NotNull(receipt);
        Assert.Equal(0, receipt.InputAt);
        Assert.Equal(8, receipt.Deadline);
    }
}
