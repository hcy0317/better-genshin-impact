using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatSkillAttemptTests
{
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
