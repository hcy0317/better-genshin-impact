using BetterGenshinImpact.GameTask.AutoFight;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class GuardianSkillSwitchPolicyTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(5, 2)]
    public void NormalizeAttemptCount_ShouldBoundRepeatedTenKeySwitchCycles(int requested, int expected)
    {
        Assert.Equal(expected, GuardianSkillSwitchPolicy.NormalizeAttemptCount(requested));
    }

    [Theory]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    public void DuplicateGuardianSkill_ShouldBeSkippedOnlyAfterPrioritySkillHandled(
        bool guardianSkillHandled,
        bool commandTargetsGuardian,
        bool isSkillCommand,
        bool expected)
    {
        Assert.Equal(
            expected,
            GuardianSkillSwitchPolicy.ShouldSkipDuplicateSkill(
                guardianSkillHandled,
                commandTargetsGuardian,
                isSkillCommand));
    }

    [Fact]
    public void PriorityGuardianSkill_ShouldNotBeRecheckedAfterItWasHandledInTheSameLoop()
    {
        Assert.False(GuardianSkillSwitchPolicy.ShouldEnsureGuardianSkill(
            guardianSkillHandled: true,
            shouldSwitch: true));
    }

    [Fact]
    public void PriorityGuardianSkill_CanRetryAfterAnUnsuccessfulAttempt()
    {
        Assert.True(GuardianSkillSwitchPolicy.ShouldEnsureGuardianSkill(
            guardianSkillHandled: false,
            shouldSwitch: true));
    }
}
