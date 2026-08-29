using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactLockExecutionPolicyTests
{
    [Theory]
    [InlineData(true, true, "Skip")]
    [InlineData(false, false, "Skip")]
    [InlineData(true, false, "Inspect")]
    [InlineData(false, true, "Inspect")]
    public void DetailStateDeterminesWhetherToggleIsNeeded(
        bool isLocked,
        bool desiredLocked,
        string expected)
    {
        Assert.Equal(expected, ArtifactLockExecutionPolicy.FromDetail(isLocked, desiredLocked).ToString());
    }

    [Fact]
    public void DetailPostconditionUsesStableFramesWithoutAMinimumDelay()
    {
        Assert.False(ArtifactLockExecutionPolicy.IsStableDetailState(2));
        Assert.True(ArtifactLockExecutionPolicy.IsStableDetailState(3));
        Assert.False(ArtifactLockExecutionPolicy.CanTreatAsStableUnchanged(4));
        Assert.True(ArtifactLockExecutionPolicy.CanTreatAsStableUnchanged(5));
        Assert.Equal(
            "Complete",
            ArtifactLockExecutionPolicy.FromTransition(
                ArtifactDetailTransitionOutcome.DesiredStable, clickCount: 1).ToString());
        Assert.Equal(
            "Retry",
            ArtifactLockExecutionPolicy.FromTransition(
                ArtifactDetailTransitionOutcome.UnchangedStable, clickCount: 1).ToString());
        Assert.Equal(
            "Fail",
            ArtifactLockExecutionPolicy.FromTransition(
                ArtifactDetailTransitionOutcome.UnchangedStable, clickCount: 3).ToString());
    }

    [Fact]
    public void LockTransitionRequiresTheButtonVisualToSettle()
    {
        var detector = new ArtifactDetailLockTransitionDetector(
            initialLocked: false,
            desiredLocked: true,
            initialVisualSignature: 10,
            tolerance: 0.5);

        Assert.Equal(ArtifactDetailTransitionOutcome.Unstable, detector.Observe(true, 20));
        Assert.Equal(ArtifactDetailTransitionOutcome.Unstable, detector.Observe(true, 24));
        Assert.Equal(ArtifactDetailTransitionOutcome.Unstable, detector.Observe(true, 24));
        Assert.Equal(ArtifactDetailTransitionOutcome.DesiredStable, detector.Observe(true, 24));
    }

    [Fact]
    public void UnchangedButtonRetriesOnlyAfterFiveStableVisualFrames()
    {
        var detector = new ArtifactDetailLockTransitionDetector(
            initialLocked: false,
            desiredLocked: true,
            initialVisualSignature: 10,
            tolerance: 0.5);

        for (var frame = 1; frame < 5; frame++)
        {
            Assert.Equal(
                ArtifactDetailTransitionOutcome.Unstable,
                detector.Observe(false, 10));
        }
        Assert.Equal(
            ArtifactDetailTransitionOutcome.UnchangedStable,
            detector.Observe(false, 10));
    }

    [Fact]
    public void LockExecutionUsesTheSameFastArtifactGridAsScanning()
    {
        var grid = ArtifactLockExecutionPolicy.CreateGridParams(
            new Size(3840, 2160),
            totalItems: 1125);

        Assert.True(grid.FastScroll);
        Assert.Equal(8, grid.Columns);
        Assert.Equal(5, grid.FastScrollRows);
        Assert.Equal(1125, grid.TotalItems);
    }
}
