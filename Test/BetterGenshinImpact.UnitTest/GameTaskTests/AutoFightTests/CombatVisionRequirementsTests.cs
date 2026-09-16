using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatVisionRequirementsTests
{
    [Theory]
    [InlineData("琴 e(required)", false)]
    [InlineData("琴 q(required)", true)]
    [InlineData("琴 e(if=q-energy-low(琴))", true)]
    [InlineData("琴 e(if=q-cd(琴))", true)]
    [InlineData("琴 e(required)\nsegment(未调用,define) { 琴 q(required) }", false)]
    [InlineData("call(使用Q,required)\nsegment(使用Q,define) { 琴 q(required) }", true)]
    public void OnlyReachableCommandsAndConditionsRequestBurstModelPreparation(string text, bool required)
    {
        Assert.Equal(required, CombatFlowProgram.Compile(text).NeedsBurstVision());
    }

    [Theory]
    [InlineData("true", false)]
    [InlineData("q-ready(琴)", true)]
    public void JsonRootConditionParticipatesInPreparationRequirements(string expression, bool required)
    {
        using var execution = new JsonCombatFlowExecution(new JsonCombatStrategy
        {
            Actions = [new() { Name = "战技", Character = "琴", Action = "e(required)",
                Condition = new() { Expression = expression } }]
        }, new NoInputGame());
        Assert.Equal(required, execution.NeedsBurstVision);
    }

    private sealed class NoInputGame : ICombatFlowGame
    {
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct) =>
            throw new InvalidOperationException("编译准备不执行输入");
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
