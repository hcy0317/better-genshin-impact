using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using System.Text.Json;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactSetCatalogTests
{
    [Fact]
    public void EveryBundledSetNameSurvivesOneInsertedOcrSymbol()
    {
        var path = Path.Combine(
            FindRepoRoot(),
            "BetterGenshinImpact",
            "GameTask",
            "ArtifactAnalysis",
            "Assets",
            "artifact-sets.zh.json");
        var names = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(path))!;
        var catalog = new ArtifactSetCatalog(names);

        foreach (var pair in names)
        {
            var noisy = pair.Value.Insert(
                Math.Max(1, pair.Value.Length / 2),
                "*");
            var expected = string.Concat(pair.Key.Split('_').Select(part =>
                char.ToUpperInvariant(part[0]) + part[1..]));
            Assert.Equal(expected, catalog.ResolveSetKey(noisy));
        }
    }

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

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "BetterGenshinImpact.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate BetterGenshinImpact.sln.");
    }
}
