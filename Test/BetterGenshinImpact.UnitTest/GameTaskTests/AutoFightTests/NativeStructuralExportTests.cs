using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class NativeStructuralExportTests
{
    [Fact]
    public void ReorderedControlCommandCanCarryItsOriginalActorExplicitly()
    {
        var program = CombatFlowProgram.Compile("""
            strategy(loop=battle)
            芙宁娜 q(if=q-ready(芙宁娜))
            琴 call(群疗,if=q-ready())
            segment(群疗,define) {
                琴 q
            }
            """);
        Assert.Equal("琴", program.Root.Nodes[1].Command.Name);
    }
}
