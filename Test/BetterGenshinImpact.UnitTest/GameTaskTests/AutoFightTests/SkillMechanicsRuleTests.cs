using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class SkillMechanicsRuleTests
{
    [Fact]
    public void LocalRuleImportRejectsUnknownSchemaFieldsWithoutChangingTheActiveRules()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skill-import-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient();
        var catalog = new CombatSkillCatalog(new SkillCatalogStore(Path.Combine(directory, "skills.db")), new GenshinDbSkillSource(client));
        var fact = new SkillFact { Id = "test.e", CharacterKey = "test", Revision = "r1", SourceUrl = "https://example.com/test" };
        catalog.Store.Import([fact], "r1", DateTimeOffset.UtcNow);
        var pack = new SkillMechanicsRulePack { Version = "v1", Rules = [new()
        {
            SkillId = fact.Id, SourceUrl = fact.SourceUrl, Dependencies = new() { [fact.Id] = SkillMechanicsRulePack.Fingerprint(fact) },
            Forms = new() { ["press"] = new() }
        }] };
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(pack);
        catalog.ImportRules(json);
        Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => catalog.ImportRules(json.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"UnknownOverride\":true")));
        Assert.Equal("v1", catalog.ReadStatus().RuleVersion);
    }

    [Fact]
    public void BuiltinRulesBindShieldToHoldAndInfusionToItsOwnPassive()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var seedPath = Path.Combine(root.FullName, "BetterGenshinImpact/GameTask/AutoFight/Assets/SkillData/builtin-skills.json");
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skill-builtin-" + Guid.NewGuid().ToString("N"));
        using var client = new HttpClient();
        var catalog = new CombatSkillCatalog(new SkillCatalogStore(Path.Combine(directory, "skills.db")), new GenshinDbSkillSource(client), seedPath);
        var snapshot = catalog.Store.ReadSnapshot();
        var shield = snapshot.Skills["zhongli.e"];
        Assert.Empty(shield.Forms["press"].Effects);
        Assert.Equal("jade-shield", shield.Forms["hold"].DefaultEffect);
        Assert.Equal(20, CombatFlowProgram.Compile("钟离 e(hold,record=护盾)", snapshot).GetTiming("钟离", "e", true)!.Duration);
        Assert.Null(CombatFlowProgram.Compile("娜维娅 e(record=附魔)", snapshot).GetTiming("娜维娅", "e")!.Duration);
        var infusion = snapshot.Skills["navia.e"].Forms["press"].Effects.Single();
        Assert.Equal("navia.passive1", infusion.DurationSkillId);
        Assert.Equal(4, snapshot.Resolve(infusion.DurationSkillId, infusion.DurationMetric));
        var refresh = snapshot.Skills["sangonomiyakokomi.q"].Forms["press"].Refreshes.Single();
        Assert.Equal(snapshot.Skills["sangonomiyakokomi.e"].Revision, refresh.ProducerRevision);
        Assert.Equal(0, catalog.ReadStatus().NeedsReviewCount);
        Assert.True(catalog.ReadStatus().VerifiedRuleCount >= 20);
    }

    [Fact]
    public void PassiveDurationNeedsAKnownUserPrerequisiteAndProfilesSurviveFactSync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skill-profile-" + Guid.NewGuid().ToString("N"));
        var store = new SkillCatalogStore(Path.Combine(directory, "skills.db"));
        var e = new SkillFact
        {
            Id = "test.e", CharacterKey = "test", Character = "娜维娅", Slot = "e", Revision = "r1", SourceUrl = "https://example.com/e",
            Metrics = new() { ["cd"] = new() { Values = [9, 8] }, ["unrelated-stack-duration"] = new() { Values = [300] } },
            Forms = new() { ["press"] = new() { CooldownMetric = "cd", Effects = [new()
            {
                Id = "infusion", DurationSkillId = "test.passive1", DurationMetric = "duration", MinimumAscension = 1
            }] } }
        };
        var passive = new SkillFact
        {
            Id = "test.passive1", CharacterKey = "test", Character = "娜维娅", Slot = "passive1", Revision = "r1",
            SourceUrl = "https://example.com/passive", Metrics = new() { ["duration"] = new() { Values = [4] } }
        };
        store.Import([e, passive], "r1", DateTimeOffset.UtcNow);
        Assert.Null(CombatFlowProgram.Compile("娜维娅 e(record=附魔)", store.ReadSnapshot()).GetTiming("娜维娅", "e")!.Duration);
        store.SetCharacterProfile(new() { CharacterKey = "test", Ascension = 1, TalentLevels = new() { ["e"] = 2 }, Reason = "用户已核对" });
        e.Revision = passive.Revision = "r2";
        store.Import([e, passive], "r2", DateTimeOffset.UtcNow);
        var timing = CombatFlowProgram.Compile("娜维娅 e(record=附魔)", new SkillCatalogStore(store.DatabasePath).ReadSnapshot()).GetTiming("娜维娅", "e")!;
        Assert.Equal(4, timing.Duration);
        Assert.Equal(8, timing.Cooldown);
    }

    [Fact]
    public void ChangedSourceRevokesVerifiedRulesWithoutChangingTheRunningSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skill-rules-" + Guid.NewGuid().ToString("N"));
        var store = new SkillCatalogStore(Path.Combine(directory, "skills.db"));
        SkillFact Fact(string revision, string description) => new()
        {
            Id = "test.e", CharacterKey = "test", Character = "测试", Slot = "e", Revision = revision,
            SourceUrl = "https://example.com/skills/test", EnglishDescription = description,
            Metrics = new() { ["duration"] = new() { Label = "Duration", SourceParameter = "param1", Values = [12] } }
        };
        var original = Fact("r1", "Creates a field.");
        var rules = new SkillMechanicsRulePack
        {
            Version = "test-v1", Rules = [new()
            {
                SkillId = "test.e", SourceUrl = original.SourceUrl,
                Dependencies = new() { [original.Id] = SkillMechanicsRulePack.Fingerprint(original) },
                Forms = new() { ["press"] = new() { Effects = [new() { Id = "field", Capability = "field", DurationMetric = "duration" }] } }
            }]
        };
        store.Import([original], "r1", DateTimeOffset.UtcNow, rulePack: rules);
        var running = store.ReadSnapshot();
        Assert.Single(running.Skills[original.Id].Forms);
        store.Import([Fact("r2", "The field now disappears after switching.")], "r2", DateTimeOffset.UtcNow);
        var reopened = new SkillCatalogStore(store.DatabasePath).ReadSnapshot();
        Assert.Empty(reopened.Skills[original.Id].Forms);
        Assert.Equal("needs-review", reopened.Skills[original.Id].MechanicsStatus);
        Assert.Equal("test-v1", reopened.Skills[original.Id].MechanicsVersion);
        Assert.Single(running.Skills[original.Id].Forms);
        Assert.Equal("r1", running.Skills[original.Id].Revision);
    }
}
