using BetterGenshinImpact.GameTask.AutoFight.Script;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatHostSelectionTests
{
    [Fact]
    public void HostPrecheckAllowsActorlessFlowButDoesNotSilentlyAcceptMissingActors()
    {
        var marker = CombatScriptParser.ParseContext("record(开始)");
        Assert.True(marker.IsAvailableForParty([]));
        var required = CombatScriptParser.ParseContext("雷神 q(required)");
        Assert.Throws<InvalidOperationException>(() => required.IsAvailableForParty(["琴"]));
        Assert.True(required.IsAvailableForParty(["雷电将军"]));
        var legacy = CombatScriptParser.ParseContext("雷神 q");
        Assert.False(legacy.IsAvailableForParty(["琴"]));
    }

    [Fact]
    public void PartySelectionPreservesDeclarationsAndOpeningInsteadOfFilteringThemAsActors()
    {
        var script = CombatScriptParser.ParseContext("""
            timing(盾,duration=20)
            call(开场,once=battle,required)
            琴 attack
            segment(start,name=开场,define)
            钟离 e(hold,timing=盾,record=护盾,required)
            segment(end)
            """);
        var selected = script.SelectForParty(["钟离", "琴", "班尼特", "香菱"]);
        Assert.Equal(script.CombatCommands.Count, selected.Count);
        Assert.Equal(Method.Timing, selected[0].Method);
        Assert.Equal(Method.Call, selected[1].Method);
        Assert.Throws<InvalidOperationException>(() => script.SelectForParty(["琴", "班尼特", "香菱", "芙宁娜"]));
    }
}
