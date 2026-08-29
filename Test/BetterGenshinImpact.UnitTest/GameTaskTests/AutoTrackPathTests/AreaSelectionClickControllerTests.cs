using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class AreaSelectionClickControllerTests
{
    [Fact]
    public async Task TryApplyAsync_RetriesUntilSelectionIsConfirmed()
    {
        var outcomes = new Queue<bool>([false, false, true]);
        var attempts = new List<int>();

        var applied = await AreaSelectionClickController.TryApplyAsync(
            maxAttempts: 3,
            async attempt =>
            {
                attempts.Add(attempt);
                await Task.Yield();
                return outcomes.Dequeue();
            });

        Assert.True(applied);
        Assert.Equal([1, 2, 3], attempts);
    }

    [Fact]
    public async Task TryApplyAsync_StopsAfterBoundedUnconfirmedClicks()
    {
        var attempts = new List<int>();

        var applied = await AreaSelectionClickController.TryApplyAsync(
            maxAttempts: 3,
            attempt =>
            {
                attempts.Add(attempt);
                return Task.FromResult(false);
            });

        Assert.False(applied);
        Assert.Equal([1, 2, 3], attempts);
    }
}
