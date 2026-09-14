using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFragmentResultTests
{
    [Fact]
    public async Task RequiredMissingActorFailsBeforeGenericInputAndPreventsPathCompletion()
    {
        var script = CombatScriptParser.ParseContext("keypress(f);钟离 e(hold)", validate: false);
        var inputs = 0;
        var result = await CombatScriptExecutor.ExecuteLegacyAsync(script, ["琴"],
            (_, _, _) => { inputs++; return new(CombatExecutionKind.Completed, "input-confirmed"); },
            CombatScriptExecutionMode.RequiredSequence, NullLogger.Instance, default);
        Assert.Equal(CombatExecutionKind.Failed, result.Kind);
        Assert.Equal(0, inputs);
        Assert.Throws<InvalidOperationException>(() => CombatScriptHandler.EnsureFragmentCompleted(result));
    }

    [Fact]
    public async Task OnlyAnExplicitLegacyTemplateMaySkipMissingAlternatives()
    {
        var script = CombatScriptParser.ParseContext("钟离 e(hold);茜特菈莉 e;莱依拉 e", validate: false);
        var actors = new List<string>();
        CombatExecutionResult Execute(CombatCommand command, CombatCommand? _, CancellationToken ct)
        {
            actors.Add(command.Name);
            return new(CombatExecutionKind.Completed, "cast-confirmed");
        }
        var strict = await CombatScriptExecutor.ExecuteLegacyAsync(script, ["钟离"], Execute,
            CombatScriptExecutionMode.RequiredSequence, NullLogger.Instance, default);
        Assert.Equal(CombatExecutionKind.Failed, strict.Kind);
        Assert.Empty(actors);
        var template = await CombatScriptExecutor.ExecuteLegacyAsync(script, ["钟离"], Execute,
            CombatScriptExecutionMode.LegacyPartyTemplate, NullLogger.Instance, default);
        CombatScriptHandler.EnsureFragmentCompleted(template);
        Assert.Equal(CombatExecutionKind.Completed, template.Kind);
        Assert.Equal(["钟离"], actors);
    }

    [Fact]
    public async Task OptionalFastSkipIsNotCompletionAndCannotMaskNoApplicableActor()
    {
        var script = CombatScriptParser.ParseContext("钟离 e(fast)", validate: false);
        var skipped = await CombatScriptExecutor.ExecuteLegacyAsync(script, ["钟离"],
            (_, _, _) => new(CombatExecutionKind.Skipped, "FAST_E_NOT_READY"),
            CombatScriptExecutionMode.LegacyPartyTemplate, NullLogger.Instance, default);
        Assert.Equal(CombatExecutionKind.Skipped, skipped.Kind);
        CombatScriptHandler.EnsureFragmentCompleted(skipped);
        var missing = await CombatScriptExecutor.ExecuteLegacyAsync(script, ["琴"],
            (_, _, _) => throw new Exception("缺队不应调用游戏端口"),
            CombatScriptExecutionMode.LegacyPartyTemplate, NullLogger.Instance, default);
        Assert.Equal("NO_APPLICABLE_ACTOR", missing.Reason);
        Assert.Throws<InvalidOperationException>(() => CombatScriptHandler.EnsureFragmentCompleted(missing));
    }

    [Theory]
    [InlineData(CombatExecutionKind.Failed)]
    [InlineData(CombatExecutionKind.Deferred)]
    public async Task EarlierCompletedInputDoesNotMaskALaterRequiredFailure(CombatExecutionKind failure)
    {
        var script = CombatScriptParser.ParseContext("琴 attack(0.1),e,attack(0.1)", validate: false);
        var calls = 0;
        var result = await CombatScriptExecutor.ExecuteLegacyAsync(script, ["琴"], (_, _, _) =>
            ++calls == 1 ? new(CombatExecutionKind.Completed, "first-done") : new(failure, "switch-or-cast-unconfirmed"),
            CombatScriptExecutionMode.RequiredSequence, NullLogger.Instance, default);
        Assert.Equal(2, calls);
        Assert.Equal(failure, result.Kind);
        Assert.Throws<InvalidOperationException>(() => CombatScriptHandler.EnsureFragmentCompleted(result));
    }

    [Fact]
    public async Task CancellationAndEnhancedRequiredActorsCannotBeDowngradedByTemplateMode()
    {
        var cancellation = new OperationCanceledException("original cancellation");
        var script = CombatScriptParser.ParseContext("琴 attack(0.1),e", validate: false);
        var actual = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await CombatScriptExecutor.ExecuteLegacyAsync(script, ["琴"], (_, _, _) => throw cancellation,
                CombatScriptExecutionMode.LegacyPartyTemplate, NullLogger.Instance, default));
        Assert.Same(cancellation, actual);
        var required = CombatScriptParser.ParseContext("琴 attack(0.1,required)", validate: false);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await CombatScriptExecutor.ExecuteLegacyAsync(required, ["琴"], (_, _, _) => new(CombatExecutionKind.Completed, "invalid"),
                CombatScriptExecutionMode.LegacyPartyTemplate, NullLogger.Instance, default));
        Assert.Equal(CombatExecutionKind.Deferred,
            CombatScriptExecutor.FromFlowResult(BetterGenshinImpact.GameTask.AutoFight.Script.Flow.CombatFlowResult.Deferred).Kind);
    }
}
