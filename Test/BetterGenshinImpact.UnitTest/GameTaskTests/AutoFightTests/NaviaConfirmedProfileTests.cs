using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class NaviaConfirmedProfileTests
{
    [Fact]
    public void DeliveredProfileGrantsOnlyConfirmedPassiveUsingBuiltinRules()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var temp = Path.Combine(Path.GetTempPath(), "bgi-navia-confirmed-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = new HttpClient();
            var catalog = new CombatSkillCatalog(new SkillCatalogStore(Path.Combine(temp, "skills.db")),
                new GenshinDbSkillSource(client), Path.Combine(root.FullName,
                    "BetterGenshinImpact/GameTask/AutoFight/Assets/SkillData/builtin-skills.json"));
            double? Duration() => CombatFlowProgram.Compile("娜维娅 e(record=附魔)", catalog.Store.ReadSnapshot())
                .GetTiming("娜维娅", "e")!.Duration;
            Assert.Null(Duration());
            catalog.ImportProfile("{\"CharacterKey\":\"navia\",\"Reason\":\"fixture without passive\"}");
            Assert.Null(Duration());
            catalog.ImportProfile("{\"CharacterKey\":\"zhongli\",\"UnlockedPassives\":[\"zhongli.passive1\"],\"Reason\":\"other character\"}");
            Assert.Null(Duration());
            var json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/Combat/navia-confirmed-profile.json"));
            var profile = CombatSkillCatalog.ParseLocalJson<CharacterSkillProfile>(json);
            Assert.Null(profile.Ascension);
            Assert.Null(profile.Constellation);
            Assert.Empty(profile.TalentLevels);
            Assert.Equal(["navia.passive1"], profile.UnlockedPassives);
            catalog.ImportProfile(json);
            Assert.Equal(4, Duration());
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }
}
