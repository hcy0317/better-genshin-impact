using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactCharacterCardReaderTests
{
    [Theory]
    [InlineData("Lv.90", 90)]
    [InlineData("等级 80", 80)]
    [InlineData("Lv 1", 1)]
    public void LevelText_ParsesSupportedCharacterLevels(string text, int expected)
    {
        Assert.True(ArtifactCharacterCardReader.TryParseLevel(text, out var level));
        Assert.Equal(expected, level);
    }

    [Fact]
    public void Favorite_RequiresACompactGoldenMarkInTheTopRightRoi()
    {
        using var plain = new Mat(new Size(115, 140), MatType.CV_8UC3, Scalar.Black);
        using var favorite = plain.Clone();
        Cv2.Rectangle(favorite, new Rect(92, 8, 10, 10), new Scalar(0, 200, 255), -1);

        Assert.False(ArtifactCharacterCardReader.IsFavorite(plain, 1));
        Assert.True(ArtifactCharacterCardReader.IsFavorite(favorite, 1));
    }

    [Theory]
    [InlineData("芙宁娜", "Furina")]
    [InlineData("胡桃", "HuTao")]
    [InlineData("雷电将军", "RaidenShogun")]
    public void CharacterName_MapsToTheArtifactCatalogKey(string name, string expected)
    {
        Assert.Equal(expected, ArtifactCharacterCardReader.ToCharacterKey(name));
    }
}
