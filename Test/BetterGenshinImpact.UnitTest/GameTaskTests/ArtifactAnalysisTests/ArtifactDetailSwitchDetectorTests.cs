using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactDetailSwitchDetectorTests
{
    [Fact]
    public void ScanDetectorCompletesOnTheFirstChangedDetailFrame()
    {
        var detector = new ArtifactScanDetailChangeDetector(100, 0.5);

        Assert.False(detector.Observe(100.2));
        Assert.True(detector.Observe(132));
    }

    [Fact]
    public void Observe_CompletesAsSoonAsTheChangedPanelBecomesStable()
    {
        var detector = new ArtifactDetailSwitchDetector(100, 0.5);

        Assert.False(detector.Observe(100, 10));
        Assert.False(detector.Observe(132, 10));
        Assert.True(detector.Observe(132, 10));
    }

    [Fact]
    public void Observe_DoesNotCompleteUntilTheLockButtonAlsoBecomesStable()
    {
        var detector = new ArtifactDetailSwitchDetector(100, 0.5);

        Assert.False(detector.Observe(132, 10));
        Assert.False(detector.Observe(132, 18));
        Assert.True(detector.Observe(132, 18));
    }

    [Fact]
    public void Observe_DoesNotCompleteBeforeAnyVisibleDetailChange()
    {
        var detector = new ArtifactDetailSwitchDetector(100, 0.5);

        Assert.False(detector.Observe(100.2, 10));
        Assert.False(detector.Observe(99.8, 10));
    }

}
