using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class GameStartupRetryPlanTests
{
    [Fact]
    public void DefaultPlanKeepsFiveMinuteWindowAndAllowsExactlyOneRetry()
    {
        var plan = GameStartupRetryPlan.Default;

        Assert.Equal(TimeSpan.FromMinutes(5), plan.AttemptWindow);
        Assert.Equal(2, plan.AttemptCount);
        Assert.Equal(1, plan.FirstAttempt);
        Assert.Equal(TimeSpan.FromMinutes(10), plan.TotalWindow);
        Assert.True(plan.AllowsRetryAfter(plan.FirstAttempt));
        Assert.False(plan.AllowsRetryAfter(2));
        Assert.False(plan.IsFinalAttempt(plan.FirstAttempt));
        Assert.True(plan.IsFinalAttempt(2));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void RetryIsGrantedOnlyBetweenTheFirstAndTheLastAttempt(int attempt, bool allowed)
    {
        Assert.Equal(allowed, GameStartupRetryPlan.Default.AllowsRetryAfter(attempt));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void OnlyTheLastAttemptIsTerminal(int attempt, bool terminal)
    {
        Assert.Equal(terminal, GameStartupRetryPlan.Default.IsFinalAttempt(attempt));
    }

    [Fact]
    public void PlanRejectsNonPositiveWindowOrAttemptCount()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameStartupRetryPlan(TimeSpan.Zero, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameStartupRetryPlan(TimeSpan.FromMinutes(-1), 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameStartupRetryPlan(TimeSpan.FromMinutes(5), 0));
    }

    [Theory]
    [InlineData(true, true, true, true, true, (int)GameStartupRecoveryAction.RearmDoorTrigger)]
    [InlineData(true, true, true, false, false, (int)GameStartupRecoveryAction.RearmDoorTrigger)]
    [InlineData(false, false, true, false, false, (int)GameStartupRecoveryAction.Reattach)]
    [InlineData(false, true, false, true, true, (int)GameStartupRecoveryAction.Reattach)]
    [InlineData(false, false, false, false, false, (int)GameStartupRecoveryAction.Unavailable)]
    [InlineData(false, false, false, true, false, (int)GameStartupRecoveryAction.Unavailable)]
    [InlineData(false, false, false, false, true, (int)GameStartupRecoveryAction.Unavailable)]
    public void RecoveryPrefersRearmingALiveWindowAndFailsClosedWithoutLinkedStart(
        bool dispatcherEnabled,
        bool contextInitialized,
        bool windowPresent,
        bool linkedStartEnabled,
        bool installPathAvailable,
        int expected)
    {
        var actual = GameStartupRecoveryPlan.Decide(
            dispatcherEnabled, contextInitialized, windowPresent, linkedStartEnabled, installPathAvailable);

        Assert.Equal((GameStartupRecoveryAction)expected, actual);
    }
}
