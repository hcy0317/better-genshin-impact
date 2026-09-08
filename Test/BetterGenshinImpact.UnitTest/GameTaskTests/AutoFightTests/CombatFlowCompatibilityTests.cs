using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowCompatibilityTests
{
    [Fact]
    public async Task LegacyRefreshPointsShareTheCompiledMaintenanceChannel()
    {
        var catalog = new SkillCatalogSnapshot(new Dictionary<string, SkillFact> { ["test.e"] = new()
        {
            Id = "test.e", Character = "钟离", Slot = "e", Revision = "fixture", Metrics = new()
            { ["hold-cd"] = new() { Values = [12] }, ["shield-duration"] = new() { Values = [20] } },
            Forms = new() { ["hold"] = new() { CooldownMetric = "hold-cd", Effects = [new()
            { Id = "shield", Capability = "shield", DurationMetric = "shield-duration" }] } }
        } });
        var program = CombatFlowProgram.Compile("strategy(loop=battle)\n钟离 e(hold,wait,refresh)\n琴 wait(1)", catalog);
        var clock = new FakeTimeProvider();
        var game = new SkillGame(CombatFlowResult.Succeeded);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(17));
        await execution.StepAsync();
        Assert.Equal(new[] { Method.Skill, Method.Skill }, game.Actions.Select(command => command.Method));
    }

    [Fact]
    public async Task DefaultWaitBudgetIncludesTheKnownCooldownWithoutChangingTheFirstInputTime()
    {
        var program = CombatFlowProgram.Compile("""
            timing(冷却参考,cd=20)
            琴 e(wait,timing=冷却参考,required,record=本场起手)
            """);
        var clock = new FakeTimeProvider();
        using var execution = new CombatFlowExecution(program, new CooldownGame(clock), clock);
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(20, execution.Context.Find("本场起手")!.OccurredAt);
    }

    [Fact]
    public async Task ExplicitWaitBudgetAllowsTheFirstSkillToWaitForItsCrossBattleCooldown()
    {
        var program = CombatFlowProgram.Compile("琴 e(wait,timeout=25,required,record=本场起手)");
        var clock = new FakeTimeProvider();
        var game = new CooldownGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(20, execution.Context.Find("本场起手")!.OccurredAt);
    }

    [Theory]
    [InlineData("钟离", CombatFlowResult.Pending)]
    [InlineData("迪奥娜", CombatFlowResult.Pending)]
    [InlineData("钟离", CombatFlowResult.Succeeded)]
    public async Task LegacyForcedRefreshUsesTheSharedConfirmedSkillContract(string actor, CombatFlowResult skillResult)
    {
        var script = CombatScriptParser.ParseContext($"{actor} e(hold,wait,refresh)\n{actor} attack(0.1,record=后继)");
        Assert.True(script.CombatCommands[0].RequiresFlow);
        var game = new SkillGame(skillResult);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile(script), game, new FakeTimeProvider());
        var succeeded = skillResult == CombatFlowResult.Succeeded;
        Assert.Equal(succeeded ? CombatFlowResult.Succeeded : CombatFlowResult.Deferred, await execution.RunRoundAsync());
        Assert.Equal(succeeded, execution.Context.Find("后继") != null);
        Assert.True(game.SkillRequired);
        Assert.True(game.SkillWaits);
    }

    private sealed class CooldownGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            return ValueTask.FromResult(action.TryBeginInput() ? CombatFlowResult.Succeeded : CombatFlowResult.Skipped);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class SkillGame(CombatFlowResult result) : ICombatFlowGame
    {
        public List<CombatCommand> Actions { get; } = [];
        public bool SkillRequired { get; private set; }
        public bool SkillWaits { get; private set; }
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command);
            if (action.Command.Method != Method.Skill) return ValueTask.FromResult(CombatFlowResult.Succeeded);
            SkillRequired = action.Command.HasFlag("required");
            SkillWaits = action.Command.HasFlag("wait");
            return ValueTask.FromResult(result);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
