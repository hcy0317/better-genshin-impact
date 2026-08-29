using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactRarityPolicyTests
{
    [Theory]
    [InlineData(49, 102, 182, 5)]
    [InlineData(217, 84, 156, 4)]
    public void Detector_MatchesTheObservedPanelColorToTheYasPalette(
        byte blue,
        byte green,
        byte red,
        int expected)
    {
        Assert.Equal(expected, ArtifactRarityDetector.DetectColor(
            new Vec3b(blue, green, red)));
    }

    [Fact]
    public void Detector_UsesTheYasArtifactStarSamplePoint()
    {
        Assert.Equal(
            new Point(1763, 149),
            ArtifactRarityDetector.SamplePosition(new Size(1920, 1080)));
    }

    [Fact]
    public void Snapshot_PreservesInventoryCountAfterLowerRaritiesAreExcluded()
    {
        var item = new ArtifactItemDto(
            0, "GladiatorsFinale", "flower", 20, 5, "hp", [], "", true);

        var snapshot = ArtifactSnapshotDto.Create(
            "102550550",
            "scan",
            "CURRENT_INVENTORY_ORDER",
            "genshin-7.0",
            [item],
            inventoryArtifactCount: 1125);

        Assert.Equal(1125, snapshot.ArtifactCount);
        Assert.Single(snapshot.Artifacts);
    }
}
