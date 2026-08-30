using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using BetterGenshinImpact.GameTask.AutoFight.Config;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactCharacterDetailsReaderTests
{
    [Fact]
    public void FixedDetailOcr_PrefersAccelerationWithoutLoadingDetection()
    {
        Assert.False(ArtifactCharacterDetailsReader.ForceCpuOcr);
        Assert.False(ArtifactCharacterDetailsReader.LoadsDetectionModel);
    }

    [Theory]
    [InlineData("等级90/90", 90)]
    [InlineData("等级90./90", 90)]
    [InlineData("等级90/90.", 90)]
    [InlineData("等级90%90", 90)]
    [InlineData("等级90％90", 90)]
    [InlineData("等级90 /.90", 90)]
    [InlineData("等级90 /*90", 90)]
    [InlineData("等级90/..90", 90)]
    [InlineData("等级90%%90", 90)]
    [InlineData("等级90190", 90)]
    [InlineData("等级90790", 90)]
    [InlineData("等级9090", 90)]
    [InlineData("等级8090", 80)]
    [InlineData("等级190", 1)]
    [InlineData("等级201·40", 20)]
    [InlineData("等级40", 40)]
    public void DetailLevel_ParsesSeparatedAndConcatenatedPairs(string text, int expected)
    {
        Assert.True(ArtifactCharacterDetailsReader.TryParseLevel(text, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData("等级8O/90")]
    [InlineData("等级90/9O")]
    [InlineData("等级809090")]
    [InlineData("等级80/80/90")]
    [InlineData("等级90%80")]
    [InlineData("等级209·40")]
    [InlineData("等级20040")]
    public void DetailLevel_RejectsDamagedOrAmbiguousText(string text)
    {
        Assert.False(ArtifactCharacterDetailsReader.TryParseLevel(text, out _));
    }

    [Theory]
    [InlineData(1920, 1080, 1466, 126, 260, 48, 1458, 204, 220, 36)]
    [InlineData(3840, 2160, 2932, 252, 520, 96, 2916, 408, 440, 72)]
    public void FixedDetailOcr_UsesTightNameAndLevelRegions(
        int width,
        int height,
        int nameX,
        int nameY,
        int nameWidth,
        int nameHeight,
        int levelX,
        int levelY,
        int levelWidth,
        int levelHeight)
    {
        var captureSize = new Size(width, height);

        Assert.Equal(
            new Rect(nameX, nameY, nameWidth, nameHeight),
            ArtifactCharacterDetailsReader.NameRegionForCapture(captureSize));
        Assert.Equal(
            new Rect(levelX, levelY, levelWidth, levelHeight),
            ArtifactCharacterDetailsReader.LevelRegionForCapture(captureSize));
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
    [InlineData("法露珊", "珐露珊")]
    [InlineData("那维来特", "那维莱特")]
    public void CharacterName_AcceptsAUniqueSingleCharacterOcrError(
        string rawText,
        string expected)
    {
        Assert.Equal(expected, ArtifactCharacterDetailsReader.ResolveCharacterName(rawText));
    }

    [Fact]
    public void EveryCatalogCharacterNameSurvivesWhitespaceAndSymbolNoise()
    {
        foreach (var name in DefaultAutoFightConfig.CombatAvatarNames)
        {
            Assert.Equal(name, ArtifactCharacterDetailsReader.ResolveCharacterName(name));
            var noisy = name.Insert(Math.Max(1, name.Length / 2), " * ");
            Assert.Equal(name, ArtifactCharacterDetailsReader.ResolveCharacterName(noisy));
        }
    }

    [Theory]
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
    public void ConfiguredMiliastraGender_MapsNicknameToTheMaleAvatar()
    {
        Assert.Equal(
            "奇偶·男性",
            ArtifactCharacterDetailsReader.ResolveCharacterName(
                "遥·", "眇", "遥", "MannequinBoy"));
    }

    [Theory]
    [InlineData("雷电将军", "雷电将军", "遥")]
    [InlineData("遥", "遥", "遥")]
    public void ConfiguredNicknames_CannotCollideWithStandardOrEachOther(
        string rawText,
        string gameNickname,
        string miliastraNickname)
    {
        Assert.Throws<InvalidOperationException>(() =>
            ArtifactCharacterDetailsReader.ResolveCharacterName(
                rawText,
                gameNickname,
                miliastraNickname));
    }

    [Fact]
    public void PartialDetail_MergesAValidLevelWithAValidNameFromTheRetryFrame()
    {
        var first = ArtifactCharacterDetailsReader.ParseRawPartial(
            "采来·",
            "等级20/40",
            favorite: false);
        var retry = ArtifactCharacterDetailsReader.ParseRawPartial(
            "莱依拉",
            "等级坏",
            favorite: true);

        var merged = first.Merge(retry).RequireComplete();

        Assert.Equal("莱依拉", merged.CharacterName);
        Assert.Equal(20, merged.Level);
        Assert.True(merged.Favorite);
    }

    [Fact]
    public void PartialDetail_UsesTheVerifiedRetryFrameFavoriteState()
    {
        var first = ArtifactCharacterDetailsReader.ParseRawPartial(
            "莱依拉", "等级20/40", favorite: true);
        var retry = ArtifactCharacterDetailsReader.ParseRawPartial(
            "莱依拉", "等级坏", favorite: false);

        Assert.False(first.Merge(retry).RequireComplete().Favorite);
    }

    [Fact]
    public void PartialDetail_MergesOnlyWhenTheRetryFrameStillShowsTheSameDetail()
    {
        Assert.True(ArtifactCharacterDetailsReader.IsSameDetailForRetry(
            0b_1010UL,
            0b_1011UL));
        Assert.False(ArtifactCharacterDetailsReader.IsSameDetailForRetry(
            0b_0000UL,
            0b_1111UL));
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
