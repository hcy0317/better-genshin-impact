using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatSkillAttemptTests
{
    [Fact]
    public void ExpiredUnconfirmedInputCanRecoverOnlyAfterTwoFreshExplicitReadyFrames()
    {
        var battle = Guid.NewGuid();
        using var attempts = new CombatSkillAttempts(battle);
        var old = attempts.TryBegin("娜维娅", Method.Burst, "old", 0, 2)!;
        attempts.Observe("娜维娅", Method.Burst, new(battle, 1, 2.1, false, true));
        Assert.True(attempts.IsOccupied("娜维娅", Method.Burst));
        attempts.Observe("娜维娅", Method.Burst, new(battle, 1, 2.5, false, true));
        Assert.True(attempts.IsOccupied("娜维娅", Method.Burst));
        attempts.Observe("娜维娅", Method.Burst, new(battle, 2, 2.6, null, null));
        attempts.Observe("娜维娅", Method.Burst, new(battle, 3, 2.7, false, true));
        Assert.True(attempts.IsOccupied("娜维娅", Method.Burst));
        attempts.Observe("娜维娅", Method.Burst, new(battle, 4, 3.0, false, true));
        Assert.False(attempts.IsOccupied("娜维娅", Method.Burst));
        Assert.False(attempts.Confirm(old.AttemptId, 3.1));
        Assert.NotNull(attempts.TryBegin("娜维娅", Method.Burst, "new", 3.1, 5));
    }
    [Fact]
    public void NativePendingObservationCreditsTheOriginalInputTimeWithoutAnotherInput()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        var command = new CombatCommand("那维莱特", "e(required,record=水滴)");
        using var attempts = new CombatSkillAttempts(context.BattleId);
        var input = new CombatFlowAction(command, context, () => true, 8);
        Assert.True(input.TryBeginInput());
        attempts.TryBegin(command.Name, command.Method, input.CommandId, input.InputAt!.Value, 8);
        clock.Advance(TimeSpan.FromSeconds(1));
        var resumed = new CombatFlowAction(command, context, () => true, 9);
        var result = NativeCombatFlowRunner.ReconcilePendingSkill(attempts, resumed, command.Name,
            () => new(context.BattleId, 1, context.Now, true, false));
        Assert.Equal(CombatFlowResult.Succeeded, result);
        Assert.Null(resumed.InputAt);
        Assert.Equal(0, resumed.EffectiveInputAt);
        Assert.Equal(7, resumed.RemainingBudget);
        Assert.True(attempts.IsOccupied(command.Name, command.Method));
    }

    [Fact]
    public void InstantOrExpiredConfirmationCannotBeCreditedAgain()
    {
        var battle = Guid.NewGuid();
        using var attempts = new CombatSkillAttempts(battle);
        var instant = attempts.TryBegin("琴", Method.Skill, "instant", 0, 2)!;
        Assert.True(attempts.Confirm(instant.AttemptId, 0.5));
        Assert.Null(attempts.TakeConfirmation("琴", Method.Skill, "instant", 1));
        var late = attempts.TryBegin("那维莱特", Method.Skill, "late", 0, 2)!;
        Assert.True(attempts.Observe("那维莱特", Method.Skill, new(battle, 1, 1, true, false)));
        Assert.Null(attempts.TakeConfirmation("那维莱特", Method.Skill, "late", 2));
        Assert.Equal(CombatSkillAttemptState.Expired, attempts.GetState("那维莱特", Method.Skill, 2));
    }

    [Fact]
    public void DelayedCooldownConfirmsOnlyTheOriginalCommandOnceWithoutReleasingThePhysicalSlot()
    {
        var battle = Guid.NewGuid();
        using var attempts = new CombatSkillAttempts(battle);
        var original = attempts.TryBegin("那维莱特", Method.Skill, "opening:e", 1, 8)!;
        Assert.True(attempts.Observe("那维莱特", Method.Skill, new(battle, 1, 1.5, true, false)));
        Assert.Null(attempts.TakeConfirmation("那维莱特", Method.Skill, "another:e", 2));
        Assert.Equal(original, attempts.TakeConfirmation("那维莱特", Method.Skill, "opening:e", 2));
        Assert.Null(attempts.TakeConfirmation("那维莱特", Method.Skill, "opening:e", 2.1));
        Assert.True(attempts.IsOccupied("那维莱特", Method.Skill));
    }

    [Fact]
    public void UnconfirmedSkillCannotBeResentFromAnotherCommandOrRevivedByALateResult()
    {
        var battle = Guid.NewGuid();
        using var attempts = new CombatSkillAttempts(battle);
        var first = attempts.TryBegin("琴", Method.Skill, commandId: "第一行", inputAt: 0, deadline: 2);
        Assert.NotNull(first);
        Assert.Null(attempts.TryBegin("琴", Method.Skill, "另一行长按", 2.1, 4));
        Assert.False(attempts.Observe("琴", Method.Skill, new(battle, 1, 2.1, true, false)));
        Assert.Null(attempts.TryBegin("琴", Method.Skill, "另一行", 2.2, 4));
        attempts.Observe("琴", Method.Skill, new(battle, 2, 8, false, true));
        var next = attempts.TryBegin("琴", Method.Skill, "下一次", 8, 10);
        Assert.NotNull(next);
        Assert.NotEqual(first.AttemptId, next.AttemptId);
        Assert.False(attempts.Confirm(first.AttemptId, 8.1));
        attempts.Dispose();
        Assert.False(attempts.Confirm(next.AttemptId, 8.1));
    }
}
