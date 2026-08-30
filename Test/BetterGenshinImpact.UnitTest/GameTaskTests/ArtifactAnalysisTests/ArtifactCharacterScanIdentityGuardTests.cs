using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactCharacterScanIdentityGuardTests
{
    [Fact]
    public void ConsecutiveUnchangedCharacterRequiresOneSwitchRetry()
    {
        var guard = new ArtifactCharacterScanIdentityGuard();
        Assert.True(guard.TryCommit("Furina"));

        Assert.True(guard.DidNotChange("Furina"));
        Assert.False(guard.DidNotChange("Noelle"));
    }

    [Fact]
    public void ConsecutiveUnchangedCharacterCannotBeCommitted()
    {
        var guard = new ArtifactCharacterScanIdentityGuard();
        Assert.True(guard.TryCommit("Furina"));

        var failure = Assert.Throws<InvalidOperationException>(() =>
            guard.TryCommit("Furina"));

        Assert.Contains("没有切换", failure.Message);
    }

    [Fact]
    public void NonAdjacentDuplicateIsSkippedAcrossOverlappingPages()
    {
        var guard = new ArtifactCharacterScanIdentityGuard();
        Assert.True(guard.TryCommit("Furina"));
        Assert.True(guard.TryCommit("Noelle"));

        Assert.False(guard.TryCommit("Furina"));
    }
}
