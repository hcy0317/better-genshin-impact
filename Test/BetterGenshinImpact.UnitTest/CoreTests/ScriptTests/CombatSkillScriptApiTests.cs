using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Microsoft.ClearScript.V8;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class CombatSkillScriptApiTests
{
    [Fact]
    public void ExistingEngineBindingCanReadFactsAndPersistProfileJsonThroughDispatcher()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bgi-skill-script-" + Guid.NewGuid().ToString("N"));
        using var http = new HttpClient();
        var store = new SkillCatalogStore(Path.Combine(directory, "skills.db"));
        store.Import([new SkillFact { Id = "jean.e", Character = "琴", CharacterKey = "jean", Slot = "e", Revision = "fixture", SourceUrl = "https://example.com/fixture/jean" }],
            "fixture", DateTimeOffset.UtcNow);
        var catalog = new CombatSkillCatalog(store, new GenshinDbSkillSource(http));
        var dispatcher = new Dispatcher(new object(), catalog, NullLogger<Dispatcher>.Instance);
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding | V8ScriptEngineFlags.EnableTaskPromiseConversion);
        engine.AddHostObject("dispatcher", dispatcher);
        Assert.Equal(1, engine.Evaluate("JSON.parse(dispatcher.readCombatSkillStatus()).SkillCount"));
        Assert.Equal("jean.e", engine.Evaluate("JSON.parse(dispatcher.readCombatSkillFacts('jean'))[0].Id"));
        engine.Execute("""
            dispatcher.importCombatSkillProfile(JSON.stringify({CharacterKey:'jean',TalentLevels:{e:8},Reason:'脚本边界测试'}));
            """);
        Assert.Equal(8, engine.Evaluate("JSON.parse(dispatcher.readCombatSkillProfiles()).jean.TalentLevels.e"));
        Assert.Equal(8, new SkillCatalogStore(store.DatabasePath).ReadSnapshot().Profiles["jean"].TalentLevels["e"]);
        Assert.Equal("function", engine.Evaluate("typeof dispatcher.syncCombatSkills"));
    }
}
