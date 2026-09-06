using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowTimingTests
{
    [Fact]
    public void DatabaseTimingTakesPrecedenceWhileMissingDatabaseUsesHeadDeclaration()
    {
        const string text = "timing(后台,duration=25,cd=18)\n芙宁娜 e(record=后台,timing=后台)";
        var fact = new SkillFact
        {
            Id = "furina.e", Character = "芙宁娜", Slot = "e", Revision = "revision",
            Metrics = new() { ["duration"] = new() { Values = [30] }, ["cd"] = new() { Values = [20] } }
        };
        var database = new SkillCatalogSnapshot(new Dictionary<string, SkillFact> { [fact.Id] = fact });
        Assert.Equal(30, CombatFlowProgram.Compile(text, database).GetTiming("芙宁娜", "e")!.Duration);
        Assert.Equal(25, CombatFlowProgram.Compile(text).GetTiming("芙宁娜", "e")!.Duration);
    }
}
