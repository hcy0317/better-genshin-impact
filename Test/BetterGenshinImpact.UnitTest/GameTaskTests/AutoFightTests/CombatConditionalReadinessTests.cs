using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatConditionalReadinessTests
{
    [Fact]
    public async Task CompletedOnceCallNeverSwitchesActorJustToPrepareItsOldCondition()
    {
        var clock = new FakeTimeProvider();
        var game = new ReadinessGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            call(开场,once=battle,if=e-ready(枫原万叶))
            琴 attack(0.1)
            segment(开场,define) { 枫原万叶 e }
            """), game, clock);
        await execution.RunRoundAsync();
        clock.Advance(TimeSpan.FromSeconds(2));
        await execution.RunRoundAsync();
        Assert.Single(game.Prepared);
        Assert.Equal(new[] { "枫原万叶:skill", "琴:attack", "琴:attack" }, game.Inputs);
    }

    [Fact]
    public async Task JsonRechecksHigherPriorityConditionsAfterPreparationChangedTheActor()
    {
        var clock = new FakeTimeProvider();
        var game = new ReadinessGame(clock);
        using var execution = new JsonCombatFlowExecution(new JsonCombatStrategy
        {
            Actions = [
                new() { Character = "琴", Index = 0, Action = "attack(0.1,required)", Condition = new() { Expression = "onfield(枫原万叶)" } },
                new() { Character = "枫原万叶", Index = 1, Action = "e(required)", Condition = new() { Expression = "e-ready(枫原万叶)" } }
            ]
        }, game, clock: clock);
        await execution.StepAsync();
        Assert.Equal(new[] { "琴:attack" }, game.Inputs);
        Assert.Single(game.Prepared);
    }

    [Fact]
    public async Task FalseConjunctionNeverPreparesAnUnknownReadinessOperand()
    {
        var clock = new FakeTimeProvider();
        var game = new ReadinessGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("枫原万叶 e(if=e-ready(枫原万叶) && false)"), game, clock);
        await execution.RunRoundAsync();
        Assert.Empty(game.Prepared);
        Assert.Empty(game.Inputs);
    }

    [Fact]
    public async Task PersistentUnknownCannotResetItsSharedProbeBudgetByStartingAnotherRound()
    {
        var clock = new FakeTimeProvider();
        var game = new ReadinessGame(clock) { NeverReady = true };
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("枫原万叶 e(if=e-ready(枫原万叶))"), game, clock);
        for (var i = 0; i < 30; i++) { await execution.RunRoundAsync(); clock.Advance(TimeSpan.FromSeconds(1)); }
        Assert.Equal(3, game.Prepared.Count);
        Assert.Empty(game.Inputs);
    }

    [Fact]
    public async Task FalseObservedReadinessAndCancelledPreparationNeverAuthorizeSkillInput()
    {
        var clock = new FakeTimeProvider();
        var game = new ReadinessGame(clock) { Ready = false };
        var program = CombatFlowProgram.Compile("枫原万叶 e(if=e-ready(枫原万叶))");
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.RunRoundAsync();
        Assert.Single(game.Prepared);
        Assert.Empty(game.Inputs);
        using var ct = new CancellationTokenSource();
        game = new ReadinessGame(clock) { OnPrepare = ct.Cancel };
        using var cancelled = new CombatFlowExecution(program, game, clock);
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await cancelled.RunRoundAsync(ct.Token));
        Assert.Empty(game.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingShieldOrAnAlreadyAtomicParentPreventsPreparation(bool atomicParent)
    {
        var clock = new FakeTimeProvider();
        var game = new ReadinessGame(clock);
        var script = atomicParent ? """
            call(外层)
            segment(外层,define,atomic) { 枫原万叶 e(if=e-ready(枫原万叶)) }
            """ : """
            timing(盾,cd=12,duration=20)
            钟离 e(if=false,record=护盾,timing=盾)
            call(聚怪,if=e-ready(枫原万叶))
            segment(聚怪,define,atomic,requires=record-active(护盾)) { 枫原万叶 e(required) }
            """;
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile(script), game, clock);
        await execution.RunRoundAsync();
        Assert.Empty(game.Prepared);
        Assert.Empty(game.Inputs);
    }

    [Fact]
    public async Task JsonEntryCanPrepareItsUnknownOffFieldReadiness()
    {
        var clock = new FakeTimeProvider();
        var game = new ReadinessGame(clock);
        using var execution = new JsonCombatFlowExecution(new JsonCombatStrategy
        {
            Actions = [new() { Character = "枫原万叶", Action = "e(required)", Condition = new() { Expression = "e-ready(枫原万叶)" } }]
        }, game, clock: clock);
        for (var i = 0; i < 5 && game.Inputs.Count == 0; i++) await execution.StepAsync();
        Assert.Equal(new[] { "枫原万叶:skill" }, game.Inputs);
    }

    [Fact]
    public async Task OffFieldReadinessCanBeObservedBeforeAdmittingTheDeclaredCall()
    {
        var clock = new FakeTimeProvider();
        var game = new ReadinessGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            call(聚怪,if=e-ready(枫原万叶))
            琴 attack(0.1)
            segment(聚怪,define) {
                枫原万叶 e(fast)
            }
            """), game, clock);

        await execution.RunRoundAsync();
        Assert.Equal(new[] { "枫原万叶:skill", "琴:attack" }, game.Inputs);
        Assert.Equal(new[] { "枫原万叶" }, game.Prepared);
    }

    private sealed class ReadinessGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public string ActiveActor { get; set; } = "琴";
        public List<string> Prepared { get; } = [];
        public List<string> Inputs { get; } = [];
        public bool? Ready { get; set; } = true;
        public bool NeverReady { get; set; }
        public Action? OnPrepare { get; set; }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor)
        {
            var target = args.FirstOrDefault()?.ToString() ?? actor;
            return function switch { "e-ready" => ActiveActor == target && !NeverReady ? Ready : null,
                "onfield" => ActiveActor == target, _ => null };
        }
        public ValueTask PrepareObservationAsync(CombatFlowAction action, string function, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Prepared.Add(action.Command.Name);
            ActiveActor = action.Command.Name;
            action.ReportActiveActor(ActiveActor);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            OnPrepare?.Invoke();
            return ValueTask.CompletedTask;
        }
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            ActiveActor = action.Command.Name;
            action.ReportActiveActor(ActiveActor);
            Inputs.Add(ActiveActor + ":" + action.Command.Method.Alias[0]);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
