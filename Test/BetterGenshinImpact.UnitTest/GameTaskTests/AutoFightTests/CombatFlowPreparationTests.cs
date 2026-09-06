using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowPreparationTests
{
    [Fact]
    public async Task ANotReadyIconWithoutEnergyEvidenceCannotResetTheObservationBudget()
    {
        var program = CombatFlowProgram.Compile("""
            香菱 q(recharge,from=班尼特,required,timeout=8,attempts=2)
            segment(start,name=供能,define)
            班尼特 e(feed=香菱)
            segment(end)
            """);
        var game = new PreparingGame { NeverRevealsEnergy = true };
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        for (var i = 0; i < 4; i++) Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Equal(new[] { "香菱", "香菱" }, game.Prepared);
        Assert.Empty(game.Actions);
    }

    [Fact]
    public async Task RechargePreparesUnknownRequiredObservationsBeforeAuthorizingTheDeclaredProducer()
    {
        var program = CombatFlowProgram.Compile("""
            香菱 q(recharge,from=班尼特,required,timeout=8,attempts=2)
            segment(start,name=供能,define)
            班尼特 e(feed=香菱)
            segment(end)
            """);
        var game = new PreparingGame();
        using var execution = new CombatFlowExecution(program, game, new FakeTimeProvider());
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.Equal(new[] { "香菱", "班尼特" }, game.Prepared);
        Assert.Equal(new[] { "班尼特:skill", "香菱:wait", "香菱:burst" }, game.Actions);
    }

    private sealed class PreparingGame : ICombatFlowGame
    {
        public List<string> Prepared { get; } = [];
        public List<string> Actions { get; } = [];
        public bool NeverRevealsEnergy { get; init; }
        private bool _charged;
        public ValueTask PrepareObservationAsync(CombatFlowAction action, string function, CancellationToken ct)
        {
            if (action.CanStart)
            {
                Prepared.Add(action.Command.Name);
                action.ReportActiveActor(action.Command.Name);
            }
            return ValueTask.CompletedTask;
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => function switch
        {
            "q-ready" => _charged ? true : Prepared.Contains(actor) ? false : null,
            "q-energy-low" => !NeverRevealsEnergy && Prepared.Contains(actor) ? !_charged : null,
            "q-cd" => Prepared.Contains(actor) ? false : null,
            "e-ready" => Prepared.Contains(actor) ? true : null,
            _ => null
        };
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command.Name + ":" + action.Command.Method.Alias[0]);
            if (action.Command.Method == Method.Skill) _charged = true;
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
