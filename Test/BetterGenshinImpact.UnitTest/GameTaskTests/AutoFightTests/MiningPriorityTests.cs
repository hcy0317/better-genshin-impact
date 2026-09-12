using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class MiningPriorityTests
{
    [Fact]
    public void UnconfirmedMiningDoesNotReturnAsCompleted()
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MiningHandler.RunMiningAction(() => "钟离 e(hold,wait)", _ => false));
        Assert.Contains("未确认", error.Message);
    }

    [Fact]
    public void MissingMinerCannotCompleteTheAction()
    {
        var ran = false;
        Assert.Throws<InvalidOperationException>(() => MiningHandler.RunMiningAction(() => null, _ => ran = true));
        Assert.False(ran);
    }

    [Fact]
    public void OriginalFailuresAndCancellationEscapeWithoutLaterInput()
    {
        foreach (var expected in new Exception[] { new OperationCanceledException("cancelled"),
                     new CombatNotFinishedException("not finished"), new InvalidOperationException("capture failed") })
        {
            var inputs = 0;
            var actual = Record.Exception(() => MiningHandler.RunMiningAction(() => "钟离 e(hold,wait),attack(0.1)", _ =>
            {
                inputs++;
                throw expected;
            }));
            Assert.Same(expected, actual);
            Assert.Equal(1, inputs);
        }
    }

    [Fact]
    public async Task CompletedRecoveryRemainsARouteRetrySignal()
    {
        var expected = Assert.IsType<CombatRecoveryCompletedException>(await Record.ExceptionAsync(() =>
            CombatRecoveryCompletedException.RecoverAsync(() => Task.CompletedTask, () => true,
                () => Task.CompletedTask, default)));
        Assert.Same(expected, Record.Exception(() => MiningHandler.RunMiningAction(
            () => "钟离 e(hold,wait)", _ => throw expected)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeOrDuringMiningPreventsLaterInput(bool cancelDuring)
    {
        using var cts = new CancellationTokenSource();
        var inputs = 0;
        if (!cancelDuring) cts.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => MiningHandler.RunMiningAction(
            () => "钟离 e(hold,wait),attack(0.1)", _ => { inputs++; cts.Cancel(); return true; }, cts.Token));
        Assert.Equal(cancelDuring ? 1 : 0, inputs);
    }

    [Fact]
    public void ConfirmedMiningCanCompleteNormally()
    {
        var commands = new List<string>();
        MiningHandler.RunMiningAction(() => "钟离 e(hold,wait)", command => { commands.Add(command.Name); return true; });
        Assert.Equal(["钟离"], commands);
    }

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
