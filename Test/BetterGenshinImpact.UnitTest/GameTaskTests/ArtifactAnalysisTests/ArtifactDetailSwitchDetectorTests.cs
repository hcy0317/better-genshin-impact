using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactDetailSwitchDetectorTests
{
    [Fact]
    public void ScanDetectorRejectsHashCollisionsButAcceptsARealPanelChange()
    {
        var detector = new ArtifactScanDetailChangeDetector(
            new ArtifactPanelSignature(0b1010UL, 0b1100UL), 1);

        Assert.False(detector.Observe(
            new ArtifactPanelSignature(0b1011UL, 0b1100UL)));
        Assert.True(detector.Observe(
            new ArtifactPanelSignature(0b0101UL, 0b1100UL)));
    }

    [Fact]
    public void CharacterDetectorIgnoresStableOldFramesUntilTheClickedDetailChangesAndStabilizes()
    {
        var detector = new ArtifactCharacterDetailSwitchDetector(0b1010UL, 1);

        Assert.False(detector.Observe(0b1010UL));
        Assert.False(detector.Observe(0b1010UL));
        Assert.False(detector.Observe(0b0101UL));
        Assert.True(detector.Observe(0b0100UL));
    }

    [Fact]
    public void PanelSignatureKeepsBothRegionsIndependentInsteadOfCancellingTheirChanges()
    {
        var baseline = new ArtifactPanelSignature(0b0000UL, 0b0000UL);
        var changed = new ArtifactPanelSignature(0b0001UL, 1UL << 42);

        Assert.Equal(2, changed.DistanceFrom(baseline));
    }

    [Fact]
    public void SameDetailCanCompleteOnlyAfterTheTargetGridCellShowsSelectionEvidence()
    {
        var detail = new ArtifactPanelSignature(0b1010UL, 0b1100UL);
        var detector = new ArtifactSameDetailSelectionDetector(
            detail, baselineSelectionScore: 0.10, minimumSelectionIncrease: 0.08);

        Assert.False(detector.Observe(detail, 0.12));
        Assert.False(detector.Observe(detail, 0.21));
        Assert.True(detector.Observe(detail, 0.22));
    }

    [Fact]
    public void GridSelectionScoreSeparatesABrightSelectedBorderFromAnOrdinaryCard()
    {
        using var ordinary = new Mat(
            new Size(102, 126), MatType.CV_8UC3, new Scalar(50, 105, 188));
        using var selected = ordinary.Clone();
        Cv2.Rectangle(selected, new Rect(0, 0, selected.Width, 5), Scalar.White, -1);
        Cv2.Rectangle(selected, new Rect(0, 0, 5, selected.Height), Scalar.White, -1);
        Cv2.Rectangle(selected, new Rect(selected.Width - 5, 0, 5, selected.Height), Scalar.White, -1);

        var ordinaryScore = ArtifactGridSelectionDetector.Score(ordinary);
        var selectedScore = ArtifactGridSelectionDetector.Score(selected);

        Assert.True(selectedScore >= ordinaryScore + 0.08);
        Assert.False(ArtifactGridSelectionDetector.IsAlreadySelected(ordinaryScore));
        Assert.True(ArtifactGridSelectionDetector.IsAlreadySelected(selectedScore));
    }

    [Theory]
    [InlineData(0.1336, false)]
    [InlineData(0.4385, true)]
    public void CharacterSelectionThresholdMatchesObservedFourKCardScores(
        double score,
        bool expected)
    {
        Assert.Equal(expected,
            ArtifactGridSelectionDetector.IsAlreadySelected(score));
    }

    [Fact]
    public void Observe_CompletesAsSoonAsTheChangedPanelBecomesStable()
    {
        var detector = new ArtifactDetailSwitchDetector(
            new ArtifactPanelSignature(0b0011UL, 0b0101UL), 1, 0.5);

        Assert.False(detector.Observe(
            new ArtifactPanelSignature(0b0011UL, 0b0101UL), 10));
        Assert.False(detector.Observe(
            new ArtifactPanelSignature(0b1100UL, 0b0101UL), 10));
        Assert.True(detector.Observe(
            new ArtifactPanelSignature(0b1100UL, 0b0101UL), 10));
    }

    [Fact]
    public void Observe_DoesNotCompleteUntilTheLockButtonAlsoBecomesStable()
    {
        var detector = new ArtifactDetailSwitchDetector(
            new ArtifactPanelSignature(0b0011UL, 0b0101UL), 1, 0.5);

        Assert.False(detector.Observe(
            new ArtifactPanelSignature(0b1100UL, 0b0101UL), 10));
        Assert.False(detector.Observe(
            new ArtifactPanelSignature(0b1100UL, 0b0101UL), 18));
        Assert.True(detector.Observe(
            new ArtifactPanelSignature(0b1100UL, 0b0101UL), 18));
    }

    [Fact]
    public void Observe_DoesNotCompleteBeforeAnyVisibleDetailChange()
    {
        var detector = new ArtifactDetailSwitchDetector(
            new ArtifactPanelSignature(0b0011UL, 0b0101UL), 1, 0.5);

        Assert.False(detector.Observe(
            new ArtifactPanelSignature(0b0010UL, 0b0101UL), 10));
        Assert.False(detector.Observe(
            new ArtifactPanelSignature(0b0011UL, 0b0101UL), 10));
    }

}
