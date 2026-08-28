using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactCharacterScanIdentityGuardTests
{
    [Fact]
    public void ConsecutiveUnchangedCharacterRequiresOneSwitchRetry()
    {
        var guard = new ArtifactCharacterScanIdentityGuard();
        guard.Commit("Furina");

        Assert.True(guard.DidNotChange("Furina"));
        Assert.False(guard.DidNotChange("Noelle"));
    }

    [Fact]
    public void ConsecutiveUnchangedCharacterCannotBeCommitted()
    {
        var guard = new ArtifactCharacterScanIdentityGuard();
        guard.Commit("Furina");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            guard.Commit("Furina"));

        Assert.Contains("没有切换", failure.Message);
    }

    [Fact]
    public void NonAdjacentDuplicateCannotBeCommittedAcrossPages()
    {
        var guard = new ArtifactCharacterScanIdentityGuard();
        guard.Commit("Furina");
        guard.Commit("Noelle");

        var failure = Assert.Throws<InvalidOperationException>(() =>
            guard.Commit("Furina"));

        Assert.Contains("已处理角色", failure.Message);
    }
}
