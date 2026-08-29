using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactInventoryNavigationTests
{
    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void RetryOpenAction_RequiresExplicitPermissionAndConfirmedMainUi(
        bool allowRetry,
        bool isMainUi,
        bool expected)
    {
        Assert.Equal(
            expected,
            ArtifactInventoryOpenPolicy.ShouldRetryOpenAction(
                allowRetry,
                isMainUi));
    }

    [Fact]
    public void IsTopAligned_RequiresACompleteFirstPageNearTheGridTop()
    {
        var aligned = Enumerable.Range(0, 32)
            .Select(index => new Rect(
                index % 8 * 140,
                10 + index / 8 * 180,
                120,
                150))
            .ToArray();
        var shifted = aligned
            .Select(rect => new Rect(rect.X, rect.Y + 45, rect.Width, rect.Height))
            .ToArray();

        Assert.True(ArtifactInventoryNavigation.IsTopAligned(
            aligned, expectedVisibleItems: 32, gridHeight: 783));
        Assert.False(ArtifactInventoryNavigation.IsTopAligned(
            shifted, expectedVisibleItems: 32, gridHeight: 783));
        Assert.False(ArtifactInventoryNavigation.IsTopAligned(
            aligned.Take(28), expectedVisibleItems: 32, gridHeight: 783));
    }

    [Theory]
    [InlineData(false, 0, false)]
    [InlineData(false, 120, true)]
    [InlineData(true, 0, true)]
    public void HasVerticalMovement_UsesShiftWhenCorrelationIsNearlyPerfect(
        bool phaseDetected,
        double shiftY,
        bool expected)
    {
        Assert.Equal(
            expected,
            ArtifactInventoryNavigation.HasVerticalMovement(
                phaseDetected, new Point2d(0, shiftY)));
    }

    [Fact]
    public async Task PrepareAsync_LeavesAnOpenArtifactInventoryUntouched()
    {
        var returnCalls = 0;
        var openCalls = 0;
        var resetCalls = 0;

        await ArtifactInventoryNavigation.PrepareAsync(
            () => true,
            () => { returnCalls++; return Task.CompletedTask; },
            () => { openCalls++; return Task.CompletedTask; },
            () => { resetCalls++; return Task.CompletedTask; });

        Assert.Equal(0, returnCalls);
        Assert.Equal(0, openCalls);
        Assert.Equal(1, resetCalls);
    }

    [Fact]
    public async Task PrepareAsync_UsesTheExistingMainUiFlowWhenInventoryIsClosed()
    {
        var steps = new List<string>();
        var inventoryOpen = false;

        await ArtifactInventoryNavigation.PrepareAsync(
            () => inventoryOpen,
            () => { steps.Add("return"); return Task.CompletedTask; },
            () => { steps.Add("open"); inventoryOpen = true; return Task.CompletedTask; },
            () => { steps.Add("reset"); return Task.CompletedTask; });

        Assert.Equal(["return", "open", "reset"], steps);
    }

    [Fact]
    public async Task PrepareAsync_EnforcesObtainedOrderBeforeResettingToTop()
    {
        var steps = new List<string>();

        await ArtifactInventoryNavigation.PrepareAsync(
            () => true,
            () => Task.CompletedTask,
            () => Task.CompletedTask,
            () => { steps.Add("reset"); return Task.CompletedTask; },
            () => { steps.Add("sort"); return Task.CompletedTask; });

        Assert.Equal(["sort", "reset"], steps);
    }

    [Fact]
    public async Task PrepareAsync_DoesNotResetScrollbarWhenInventoryFailedToOpen()
    {
        var resetCalls = 0;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ArtifactInventoryNavigation.PrepareAsync(
                () => false,
                () => Task.CompletedTask,
                () => Task.CompletedTask,
                () => { resetCalls++; return Task.CompletedTask; }));

        Assert.Contains("未能打开圣遗物背包", exception.Message);
        Assert.Equal(0, resetCalls);
    }
}
