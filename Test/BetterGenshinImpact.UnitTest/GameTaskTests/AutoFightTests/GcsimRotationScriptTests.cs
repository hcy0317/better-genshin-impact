using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class GcsimRotationScriptTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(10)]
    [InlineData(6)]
    public void AllExecutionPresetsRetainRequiredActionsAndCompile(int timeout)
    {
        var text = $"""
                    segment(start,required,timeout=120)
                    安柏 e(wait,required,timeout={timeout})
                    安柏 attack(2.0,required)
                    凯亚 q(required,timeout={timeout})
                    segment(end)
                    """;
        // Public parser/program validation only. No game, capture or input port.
        Assert.NotNull(CombatFlowProgram.Compile(text));
    }
}
