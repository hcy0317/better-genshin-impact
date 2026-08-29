using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactSetCatalogTests
{
    [Fact]
    public void ResolveSetKey_AcceptsOneUniqueOcrErrorInTheLocalizedName()
    {
        var catalog = new ArtifactSetCatalog(new Dictionary<string, string>
        {
            ["ocean_hued_clam"] = "海染砗磲",
            ["heart_of_depth"] = "沉沦之心"
        });

        Assert.Equal("OceanHuedClam", catalog.ResolveSetKey("海染砥磲"));
        Assert.Equal("OceanHuedClam", catalog.ResolveSetKey("海染砥磲：2件套"));
    }

    [Fact]
    public void ResolveSetKey_AcceptsAUniqueTwoCharacterPrefixWhenTheTailCollapses()
    {
        var catalog = new ArtifactSetCatalog(new Dictionary<string, string>
        {
            ["ocean_hued_clam"] = "海染砗磲",
            ["heart_of_depth"] = "沉沦之心",
            ["song_of_days_past"] = "昔时之歌"
        });

        Assert.Equal("OceanHuedClam", catalog.ResolveSetKey(
            "海染碟：\n2件套：治疗加成提高15%"));
    }

    [Fact]
    public void ResolveSetKey_AcceptsTwoOcrErrorsOnlyForAUniqueNearestName()
    {
        var catalog = new ArtifactSetCatalog(new Dictionary<string, string>
        {
            ["echoes_of_an_offering"] = "来歆余响",
            ["nymphs_dream"] = "水仙之梦",
            ["gilded_dreams"] = "饰金之梦"
        });

        Assert.Equal("EchoesOfAnOffering", catalog.ResolveSetKey(
            "来款余啊：均市大据京1021"));
    }

    [Fact]
    public void ResolveSetKey_DoesNotGuessWhenNearestNamesTie()
    {
        var catalog = new ArtifactSetCatalog(new Dictionary<string, string>
        {
            ["first"] = "甲乙丙丁",
            ["second"] = "甲乙戊己"
        });

        Assert.Throws<InvalidDataException>(() => catalog.ResolveSetKey("甲乙丙己"));
    }
}
