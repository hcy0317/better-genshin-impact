using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowConditionTests
{
    [Theory]
    [InlineData("q-ready")]
    [InlineData("q-energy-low")]
    [InlineData("q-cd")]
    [InlineData("e-ready")]
    [InlineData("e-cd")]
    [InlineData("low-hp")]
    [InlineData("onfield")]
    [InlineData("in-party")]
    public async Task ConditionAliasesResolveToTheSameCharacterAsActionAliases(string function)
    {
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile($"雷神 wait(0.1,if={function}(雷神),record=触发)"),
            new CanonicalActorGame(), new FakeTimeProvider());
        await execution.RunRoundAsync();
        Assert.NotNull(execution.Context.Find("触发"));
    }

    private sealed class CanonicalActorGame : ICombatFlowGame
    {
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) =>
            args.FirstOrDefault()?.ToString() == "雷电将军" && actor == "雷电将军";
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct) =>
            ValueTask.FromResult(action.TryBeginInput() ? CombatFlowResult.Succeeded : CombatFlowResult.Skipped);
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData("q-ready(琴,班尼特)")]
    [InlineData("call-index()")]
    [InlineData("odd(1,2)")]
    [InlineData("t(3)")]
    [InlineData("min()")]
    public void WrongFunctionArityIsRejectedBeforeObservingTheGame(string condition)
    {
        Assert.Throws<FormatException>(() => ConditionEvaluator.Compile(condition));
    }

    [Fact]
    public void CompiledConditionPreservesUnknownThroughNegationAndShortCircuit()
    {
        var condition = ConditionEvaluator.Compile("!(record-remaining(窗口)>3) || e-ready(班尼特)");
        Assert.Null(condition.EvaluateBoolean((name, args) => name == "e-ready" ? false : null));
        Assert.True(condition.EvaluateBoolean((name, args) => name == "e-ready" ? true : null));
    }
}
