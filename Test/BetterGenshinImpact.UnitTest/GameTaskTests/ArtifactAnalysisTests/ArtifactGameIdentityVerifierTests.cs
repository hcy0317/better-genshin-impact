using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactGameIdentityVerifierTests
{
    [Theory]
    [InlineData("UID: 102550550", "102550550")]
    [InlineData("UID  123456789012", "123456789012")]
    public void TryParseUid_AcceptsOnlyACompleteUid(string text, string expected)
    {
        Assert.True(ArtifactGameIdentityVerifier.TryParseUid(text, out var uid));
        Assert.Equal(expected, uid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("UID: 12345")]
    [InlineData("UID: 1234567890123")]
    public void TryParseUid_RejectsMissingOrImplausibleValues(string text)
    {
        Assert.False(ArtifactGameIdentityVerifier.TryParseUid(text, out _));
    }
}
