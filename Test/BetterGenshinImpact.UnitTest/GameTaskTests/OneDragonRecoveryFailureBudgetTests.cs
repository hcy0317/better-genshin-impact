using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

/// <summary>
/// 一条龙恢复失败预算：单次失败只跳过该任务，连续多次失败才回到"停止后续任务"。
/// </summary>
public class OneDragonRecoveryFailureBudgetTests
{
    [Fact]
    public void OnlyConsecutiveFailuresExhaustTheBudget()
    {
        var budget = new OneDragonRecoveryFailureBudget(maxConsecutiveFailures: 2);

        Assert.True(budget.TryTolerateFailure());
        Assert.Equal(1, budget.ConsecutiveFailures);
        Assert.True(budget.TryTolerateFailure());
        Assert.Equal(2, budget.ConsecutiveFailures);
        Assert.False(budget.TryTolerateFailure());
        Assert.Equal(3, budget.ConsecutiveFailures);
    }

    [Fact]
    public void ResetAfterASuccessfulStepRestoresTheFullBudget()
    {
        var budget = new OneDragonRecoveryFailureBudget(maxConsecutiveFailures: 2);

        Assert.True(budget.TryTolerateFailure());
        Assert.True(budget.TryTolerateFailure());
        budget.Reset();

        Assert.Equal(0, budget.ConsecutiveFailures);
        Assert.True(budget.TryTolerateFailure());
        Assert.True(budget.TryTolerateFailure());
        Assert.False(budget.TryTolerateFailure());
    }
}
