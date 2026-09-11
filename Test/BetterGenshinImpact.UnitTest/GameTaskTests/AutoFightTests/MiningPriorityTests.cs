using BetterGenshinImpact.GameTask.AutoPathing.Handler;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class MiningPriorityTests
{
    [Theory]
    [InlineData("娜维娅")]
    [InlineData("莉奈娅")]
    [InlineData("诺艾尔")]
    public void ZhongliHoldSkillWinsOverOtherMiners(string other)
    {
        var members = new HashSet<string> { other, "钟离" };
        Assert.Equal("钟离 e(hold,wait)", MiningHandler.SelectMiningAction(members.Contains));
    }

    [Fact]
    public void WithoutZhongliExistingFallbackOrderAndNoMatchArePreserved()
    {
        Assert.Equal("娜维娅 attack(1.25)", MiningHandler.SelectMiningAction(name => name == "娜维娅"));
        Assert.Null(MiningHandler.SelectMiningAction(_ => false));
    }
}
