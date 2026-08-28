using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactCharacterDetailsReaderTests
{
    [Fact]
    public void FixedDetailOcr_UsesTheCpuProviderPolicy()
    {
        Assert.True(ArtifactCharacterDetailsReader.ForceCpuOcr);
        Assert.False(ArtifactCharacterDetailsReader.LoadsDetectionModel);
    }

    [Theory]
    [InlineData("等级90/90", 90)]
    [InlineData("等级9090", 90)]
    [InlineData("等级8090", 80)]
    [InlineData("等级90790", 90)]
    [InlineData("等级190", 1)]
    [InlineData("等级40", 40)]
    public void DetailLevel_ParsesSeparatedAndConcatenatedPairs(string text, int expected)
    {
        Assert.True(ArtifactCharacterDetailsReader.TryParseLevel(text, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("珐露珊", "珐露珊")]
    [InlineData(" 珐露珊\n", "珐露珊")]
    [InlineData("雷电将军", "雷电将军")]
    public void CharacterName_RequiresTheExactStandardName(
        string rawText,
        string expected)
    {
        Assert.Equal(expected, ArtifactCharacterDetailsReader.ResolveCharacterName(rawText));
    }

    [Theory]
    [InlineData("法露珊")]
    [InlineData("亚")]
    public void CharacterName_RejectsPartialOrFuzzyMatches(string rawText)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ArtifactCharacterDetailsReader.ResolveCharacterName(rawText));
    }

    [Fact]
    public void ConfiguredGameNickname_MapsTheRightPanelNameToTraveler()
    {
        Assert.Equal("旅行者", ArtifactCharacterDetailsReader.ResolveCharacterName("眇·", "眇"));
    }

    [Fact]
    public void ConfiguredMiliastraNickname_MapsTheRightPanelNameToTheMiliastraAvatar()
    {
        Assert.Equal(
            "奇偶·女性",
            ArtifactCharacterDetailsReader.ResolveCharacterName("遥·", "眇", "遥"));
    }

    [Fact]
    public void Favorite_ReadsOnlyTheFixedRightPanelStar()
    {
        using var plain = new Mat(new Size(1920, 1080), MatType.CV_8UC3, Scalar.Black);
        using var favorite = plain.Clone();
        Cv2.Rectangle(favorite, new Rect(1408, 996, 24, 24), new Scalar(0, 200, 255), -1);

        Assert.False(ArtifactCharacterDetailsReader.IsFavorite(plain));
        Assert.True(ArtifactCharacterDetailsReader.IsFavorite(favorite));
    }
}
