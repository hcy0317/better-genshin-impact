using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactFastTextParserTests
{
    [Theory]
    [InlineData("击率", "3.5%", "critRate_", 3.5)]
    [InlineData("攻击力", "46.6%", "atk_", 46.6)]
    [InlineData("生命值", "4,780", "hp", 4780)]
    [InlineData("元素充能效率", "16.2%", "enerRech_", 16.2)]
    public void ParseAffix_HandlesFixedRegionOcrText(
        string name,
        string value,
        string expectedKey,
        double expectedValue)
    {
        var result = ArtifactFastTextParser.ParseAffix(name, value);

        Assert.Equal(expectedKey, result.Key);
        Assert.Equal(expectedValue, result.Value, 3);
    }

    [Theory]
    [InlineData("生之花", "flower")]
    [InlineData("死之羽", "plume")]
    [InlineData("时之沙", "sands")]
    [InlineData("空之杯", "goblet")]
    [InlineData("理之冠", "circlet")]
    public void ParseSlot_MapsLocalizedSlot(string text, string expected)
    {
        Assert.Equal(expected, ArtifactFastTextParser.ParseSlot(text));
    }

    [Theory]
    [InlineData("圣遗物强化素材", true)]
    [InlineData("生之花", false)]
    [InlineData("四星圣遗物", false)]
    public void IsEnhancementMaterial_OnlyMatchesArtifactExperienceItems(
        string text,
        bool expected)
    {
        Assert.Equal(expected, ArtifactFastTextParser.IsEnhancementMaterial(text));
    }

    [Theory]
    [InlineData("+20", 20)]
    [InlineData("0", 0)]
    public void ParseLevel_ExtractsLevel(string text, int expected)
    {
        Assert.Equal(expected, ArtifactFastTextParser.ParseLevel(text));
    }

    [Theory]
    [InlineData("· 暴击率+3.1%", true)]
    [InlineData("昔时之歌：", false)]
    [InlineData("流放者：", false)]
    [InlineData("2件套：治疗加成提高15%", false)]
    public void TryParseAffixLine_RejectsSetNamesAndDescriptions(
        string text,
        bool expected)
    {
        Assert.Equal(expected, ArtifactFastTextParser.TryParseAffixLine(
            text, out _));
    }

    [Theory]
    [InlineData("· 元素充能效率+4.5%（待激活）")]
    [InlineData("元素充能效率 +4.5% (未激活)")]
    public void ParseAffixLine_PreservesDormantMarker(string text)
    {
        var result = ArtifactFastTextParser.ParseAffixLine(text);

        Assert.Equal("enerRech_", result.Key);
        Assert.Equal(4.5, result.Value, 3);
        Assert.True(result.Dormant);
    }

    [Fact]
    public void ParseAffixLine_NormalLineRemainsActive()
    {
        Assert.False(ArtifactFastTextParser.ParseAffixLine("· 暴击率+3.1%").Dormant);
    }

    [Fact]
    public void ApplyDormantFourthLine_MarksOnlyLevelZeroFourthAffix()
    {
        ArtifactSubstatDto[] substats =
        [
            new("critRate_", 3.1),
            new("critDMG_", 6.2),
            new("def", 19),
            new("enerRech_", 4.5)
        ];

        var marked = ArtifactFastTextParser.ApplyDormantFourthLine(
            substats, 0, "元素充能效率+4.5%（待激活）");

        Assert.False(marked[2].Dormant);
        Assert.True(marked[3].Dormant);
        Assert.All(ArtifactFastTextParser.ApplyDormantFourthLine(
            substats, 20, "元素充能效率+4.5%（待激活）"),
            substat => Assert.False(substat.Dormant));
    }
}
