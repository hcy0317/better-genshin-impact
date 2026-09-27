using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class InventoryOpenOutcomeTests
{
    [Fact]
    public async Task AnUnconfirmedInventoryPageCannotStartCounting()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CountInventoryItem.RequireInventoryOpenAsync(() => Task.FromResult(false), default));
    }

    [Fact]
    public async Task AConfirmedPageMayProceed()
    {
        await CountInventoryItem.RequireInventoryOpenAsync(() => Task.FromResult(true), default);
    }

    [Fact]
    public async Task CancellationPreventsOpeningAndIsNotConvertedToPageFailure()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CountInventoryItem.RequireInventoryOpenAsync(() => throw new Exception("must not open"), cts.Token));
    }
}
