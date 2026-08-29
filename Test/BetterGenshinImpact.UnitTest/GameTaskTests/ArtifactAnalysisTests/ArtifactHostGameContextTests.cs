using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactHostGameContextTests
{
    [Fact]
    public async Task NotReady_StartsTheExistingGameTaskContextBeforeScanning()
    {
        var ready = false;

        await ArtifactHostGameContext.EnsureReadyAsync(
            () => { ready = true; return Task.CompletedTask; },
            () => ready);

        Assert.True(ready);
    }

    [Fact]
    public async Task FailedInitializationProducesAnExplicitErrorInsteadOfNullReference()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ArtifactHostGameContext.EnsureReadyAsync(
                () => Task.CompletedTask,
                () => false));

        Assert.Contains("截图器", error.Message);
    }
}
