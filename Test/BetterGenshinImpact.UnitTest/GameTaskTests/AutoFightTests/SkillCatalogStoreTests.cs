using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class SkillCatalogStoreTests
{
    [Fact]
    public void UserMetricPrecedenceKeepsPerFieldSourcesAndReportsTimingConflicts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skill-precedence-" + Guid.NewGuid().ToString("N"));
        var store = new SkillCatalogStore(Path.Combine(directory, "skills.db"));
        store.Import([new()
        {
            Id = "test.e", CharacterKey = "test", Character = "钟离", Slot = "e", Revision = "r1", SourceUrl = "https://example.com/test",
            Metrics = new() { ["hold-cd"] = new() { Values = [12] }, ["duration"] = new() { Values = [20] } }
        }], "r1", DateTimeOffset.UtcNow);
        store.SetMetricOverride("test.e", "hold-cd", new() { Values = [11] }, "用户校准");
        var program = CombatFlowProgram.Compile("""
            timing(兜底,cd=3,duration=8)
            钟离 e(hold,timing=兜底,record=护盾)
            """, store.ReadSnapshot());
        var timing = program.GetTiming("钟离", "e", true)!;
        Assert.Equal(11, timing.Cooldown);
        Assert.Equal(20, timing.Duration);
        Assert.Equal("user-override:test.e/hold-cd", timing.CooldownSource);
        Assert.Equal("database:r1/test.e/duration", timing.DurationSource);
        Assert.Equal(2, program.Diagnostics.Count);
    }

    [Fact]
    public void CancelledImportCannotReplaceTheCommittedBatch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skill-cancel-" + Guid.NewGuid().ToString("N"));
        var store = new SkillCatalogStore(Path.Combine(directory, "skills.db"));
        SkillFact Fact(string revision) => new() { Id = "test.e", CharacterKey = "test", Revision = revision, SourceUrl = "https://example.com/test" };
        store.Import([Fact("r1")], "r1", DateTimeOffset.UtcNow);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => store.Import([Fact("r2")], "r2", DateTimeOffset.UtcNow,
            cancellationToken: cancellation.Token));
        Assert.Equal("r1", store.ReadSnapshot().Skills["test.e"].Revision);
    }

    [Fact]
    public void ReplacingACharacterBatchRemovesStaleFactsButPreservesOverridesHistoryAndOtherCharacters()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skills-" + Guid.NewGuid().ToString("N"));
        var store = new SkillCatalogStore(Path.Combine(directory, "skills.db"));
        SkillFact Fact(string key, string slot, string revision, double seconds) => new()
        {
            Id = key + "." + slot, CharacterKey = key, Character = key, Slot = slot, Revision = revision,
            SourceUrl = "https://github.com/theBowja/genshin-db",
            Metrics = new() { ["cd"] = new() { Values = [seconds] } }
        };
        store.Import([Fact("one", "e", "r1", 12), Fact("one", "passive1", "r1", 2), Fact("two", "e", "r1", 8)], "r1", DateTimeOffset.UtcNow);
        store.SetMetricOverride("one.e", "cd", new() { Values = [10] }, "已核对的用户修正");
        var running = store.ReadSnapshot();
        store.Import([Fact("one", "e", "r2", 15)], "r2", DateTimeOffset.UtcNow);
        var current = new SkillCatalogStore(store.DatabasePath).ReadSnapshot();
        Assert.False(current.Skills.ContainsKey("one.passive1"));
        Assert.Equal("r1", current.Skills["two.e"].Revision);
        Assert.Equal(10, current.Resolve("one.e", "cd"));
        Assert.Equal(15, store.ReadSnapshot(useOverrides: false).Resolve("one.e", "cd"));
        Assert.True(running.Skills.ContainsKey("one.passive1"));
        Assert.True(store.ReadSnapshot("r1").Skills.ContainsKey("one.passive1"));
    }

    [Fact]
    public void RepeatedLabelsPreserveBothVariantsWithoutChoosingAnAmbiguousDefault()
    {
        var english = Newtonsoft.Json.Linq.JObject.Parse("""
            {"name":"Test","combat2":{"name":"E","attributes":{"labels":["Trigger Interval|{param1:F1}s","Trigger Interval|{param2:F1}s"]}},
             "combat3":{"name":"Q","attributes":{"labels":["CD|{param1:F1}s"]}}}
            """);
        var chinese = Newtonsoft.Json.Linq.JObject.Parse("{\"name\":\"测试\"}");
        var stats = Newtonsoft.Json.Linq.JObject.Parse("{\"combat2\":{\"param1\":[1],\"param2\":[2]},\"combat3\":{\"param1\":[12]}}");
        var skill = GenshinDbSkillSource.ParseCharacter("test", "revision", english, chinese, stats).Single(s => s.Slot == "e");
        Assert.Equal(1, skill.Metrics["trigger-interval-param1"].Values[0]);
        Assert.Equal(2, skill.Metrics["trigger-interval-param2"].Values[0]);
        Assert.False(skill.Metrics.ContainsKey("trigger-interval"));
    }

    [Fact]
    public void ImportedSkillSurvivesReopeningTheDatabase()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skills-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "skills.db");
        var fact = new SkillFact
        {
            Id = "zhongli.e", CharacterKey = "zhongli", Character = "钟离", Slot = "e", Name = "地心",
            SourceUrl = "https://github.com/theBowja/genshin-db", Revision = "test-revision",
            Metrics = new() { ["hold-cd"] = new SkillMetric { Values = [12] } }
        };
        new SkillCatalogStore(path).Import([fact], "test-revision", DateTimeOffset.UtcNow);
        var reopened = new SkillCatalogStore(path).ReadSnapshot();
        Assert.Equal(12, reopened.Resolve("zhongli.e", "hold-cd", 1));
        Assert.Equal("test-revision", reopened.Skills["zhongli.e"].Revision);
    }
}
