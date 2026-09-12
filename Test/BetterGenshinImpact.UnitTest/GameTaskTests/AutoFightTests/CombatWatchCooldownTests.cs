using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatWatchCooldownTests
{
    [Fact]
    public async Task ConfirmedCooldownCanDeferMoreThanThreeTimesThenRenewShieldAndResumeOutput()
    {
        var clock = new FakeTimeProvider();
        var game = new CoolingShieldGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            strategy(loop=battle)
            timing(盾,cd=12,duration=20)
            钟离 e(hold,wait,timing=盾,record=护盾,maintain=护盾,watch=护盾,watch-mode=call,watch-target=补盾,before=18,required)
            琴 attack(0.1,keep=护盾)
            segment(补盾,define) {
                钟离 e(hold,wait,timing=盾,record=护盾,required)
            }
            """), game, clock);

        for (var step = 0; step < 200 && game.OutputsAfterRenewal == 0; step++) await execution.StepAsync();

        Assert.True(game.OutputsAfterRenewal > 0, "A known cooldown must not permanently exhaust the shield watch after three observations");
        Assert.True(game.DeferredObservations > 3,
            $"deferred={game.DeferredObservations}, inputs={game.ShieldInputs}, now={execution.Context.Now}, renewal={execution.Context.Find("护盾")?.OccurredAt}");
        Assert.Equal(2, game.ShieldInputs);
        Assert.InRange(execution.Context.Find("护盾")!.OccurredAt, 12, 15);
    }

    [Fact]
    public async Task DeferredWatchKeepsItsOriginalDeadlineWhenReadinessNeverArrives()
    {
        var clock = new FakeTimeProvider();
        var game = new CoolingShieldGame(clock) { ReadyAt = double.PositiveInfinity };
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            strategy(loop=battle)
            timing(盾,cd=12,duration=20)
            钟离 e(hold,wait,timing=盾,record=护盾,maintain=护盾,watch=护盾,watch-mode=call,watch-target=补盾,before=18,required)
            琴 attack(0.1,keep=护盾)
            segment(补盾,define) { 钟离 e(hold,wait,timing=盾,record=护盾,required) }
            """), game, clock);
        for (var step = 0; step < 200; step++) await execution.StepAsync();

        Assert.Equal(1, game.ShieldInputs);
        Assert.Equal(0, game.OutputsAfterRenewal);
        Assert.True(game.DeferredObservations > 3);
        Assert.All(game.DeferredDeadlines, deadline => Assert.Equal(game.DeferredDeadlines[0], deadline));
        Assert.InRange(game.DeferredDeadlines[0], 15, 18);
    }

    [Theory]
    [InlineData("冰")]
    [InlineData("采集")]
    [InlineData("草")]
    [InlineData("风")]
    [InlineData("火")]
    [InlineData("矿物")]
    [InlineData("雷")]
    [InlineData("水")]
    [InlineData("岩")]
    public void ActualShippedStrategyRetainsItsNativeFlowContract(string name)
    {
        var program = CombatFlowProgram.Compile(ReadStrategy(name));
        Assert.True(program.Loop);
        Assert.NotEmpty(program.Actors);
    }

    [Fact]
    public async Task ActualWaterStrategyResumesItsSprayAfterCooldownMaintenance()
    {
        var clock = new FakeTimeProvider();
        var game = new WaterGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile(ReadStrategy("水")), game, clock);
        for (var step = 0; step < 400 && game.SpraysAfterRenewal == 0; step++) await execution.StepAsync();

        Assert.True(game.SpraysAfterRenewal > 0, $"Water output did not resume; time={execution.Context.Now}, shieldInputs={game.ShieldInputs}");
        Assert.Equal(2, game.ShieldInputs);
    }

    private static string ReadStrategy(string name)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root!.FullName, "BetterGenshinImpact", "User", "AutoFight", $"00-{name}.txt"));
    }

    private sealed class WaterGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        private double _readyAt;
        private bool _jeanBurstUsed;
        public int ShieldInputs { get; private set; }
        public int SpraysAfterRenewal { get; private set; }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor)
        {
            var target = args.FirstOrDefault()?.ToString() ?? actor;
            return function switch
            {
                "in-party" => true,
                "q-ready" => target == "琴" && !_jeanBurstUsed,
                "low-hp" or "q-energy-low" or "q-cd" => false,
                _ => null
            };
        }
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var command = action.Command;
            if (command.Name == "钟离" && command.Method == Method.Skill && ShieldInputs > 0 && action.Now < _readyAt)
            {
                clock.Advance(TimeSpan.FromSeconds(.3));
                return ValueTask.FromResult(CombatFlowResult.Deferred);
            }
            Assert.True(action.TryBeginInput());
            var seconds = .02;
            if (command.Name == "钟离" && command.Method == Method.Skill)
            {
                ShieldInputs++;
                _readyAt = action.Now + 12;
                seconds = 2.2;
            }
            else if (command.Name == "琴" && command.Method == Method.Burst) { _jeanBurstUsed = true; seconds = 3.2; }
            else if (command.Method == Method.Skill) seconds = command.Name == "芙宁娜" ? 1.2 : .7;
            else if (command.Method == Method.Wait && command.Args?.Count > 0)
                seconds = double.Parse(command.Args[0], System.Globalization.CultureInfo.InvariantCulture);
            else if (command.Method == Method.KeyUp && command.Name == "那维莱特" && ShieldInputs > 1) SpraysAfterRenewal++;
            clock.Advance(TimeSpan.FromSeconds(seconds));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            clock.Advance(TimeSpan.FromSeconds(.05));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CoolingShieldGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public int DeferredObservations { get; private set; }
        public int ShieldInputs { get; private set; }
        public int OutputsAfterRenewal { get; private set; }
        public double ReadyAt { get; init; } = 12;
        public List<double> DeferredDeadlines { get; } = [];

        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (action.Command.Method == Method.Skill)
            {
                if (ShieldInputs == 1 && action.Now < ReadyAt)
                {
                    DeferredObservations++;
                    DeferredDeadlines.Add(action.AbsoluteDeadline);
                    clock.Advance(TimeSpan.FromSeconds(.25));
                    return ValueTask.FromResult(CombatFlowResult.Deferred);
                }
                Assert.True(action.TryBeginInput());
                ShieldInputs++;
                clock.Advance(TimeSpan.FromSeconds(.25));
            }
            else
            {
                Assert.True(action.TryBeginInput());
                if (action.Command.Method == Method.Attack && ShieldInputs > 1) OutputsAfterRenewal++;
                clock.Advance(TimeSpan.FromSeconds(2));
            }
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }

        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            clock.Advance(TimeSpan.FromSeconds(.25));
            return ValueTask.CompletedTask;
        }
    }
}
