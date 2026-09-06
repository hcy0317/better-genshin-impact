using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class JsonCombatFlowTests
{
    [Fact]
    public async Task JsonHostRootsCountAsReachableRepeatingMaintenanceOutsideTheOpening()
    {
        var game = new PriorityGame();
        using var execution = new JsonCombatFlowExecution(new()
        {
            Info = new() { Declarations = ["""
                timing(盾窗,duration=20)
                segment(开场,define) { 钟离 e(required,record=护盾,timing=盾窗,watch=护盾,before=4) }
                """] },
            Actions = [new() { Character = "钟离", Action = "strategy(loop=battle),call(开场,once=battle,required),e(record=护盾,timing=盾窗,maintain=护盾,watch=护盾,before=4)" }]
        }, game, clock: new FakeTimeProvider());
        await execution.StepAsync();
        Assert.NotNull(execution.Context.Find("护盾"));
        Assert.Equal(new[] { Method.Skill }, game.Actions);
    }

    [Fact]
    public async Task AnUnproductivePriorityRootYieldsToUsefulOutputAndRegainsPriorityWhenActionable()
    {
        var game = new PriorityGame();
        using var execution = new JsonCombatFlowExecution(new() { Actions = [
            new() { Name = "高优先Q", Index = 0, Character = "琴", Action = "q(if=q-ready(琴),record=已放Q)" },
            new() { Name = "可用短打", Index = 1, Character = "琴", Action = "attack(0.1,record=短打)" }
        ] }, game, clock: new FakeTimeProvider());
        for (var step = 0; step < 8 && execution.Context.Find("短打") == null; step++) await execution.StepAsync();
        Assert.NotNull(execution.Context.Find("短打"));
        Assert.Equal(new[] { Method.Attack }, game.Actions);
        game.BurstReady = true;
        for (var step = 0; step < 8 && execution.Context.Find("已放Q") == null; step++) await execution.StepAsync();
        Assert.Equal(new[] { Method.Attack, Method.Burst }, game.Actions);
        Assert.NotNull(execution.Context.Find("已放Q"));
    }

    [Fact]
    public void JsonActionsCannotCallAnotherCompilerOwnedRootToBypassItsCondition()
    {
        var strategy = new JsonCombatStrategy { Actions = [
            new() { Name = "入口", Character = "琴", Action = "call('$json-root:1')" },
            new() { Name = "受限输出", Character = "琴", Action = "attack(1)", Condition = new() { Expression = "false" } }
        ] };
        var error = Assert.Throws<FormatException>(() => new JsonCombatFlowExecution(strategy, new PriorityGame()));
        Assert.Contains("保留", error.Message);
        Assert.Contains("JSON.actions[0].action", error.Message);
    }

    [Fact]
    public void ASharedOpeningGateCannotBeForgedBeforeTheOpeningActuallyCompletes()
    {
        var strategy = new JsonCombatStrategy
        {
            Info = new() { Declarations = ["""
                segment(start,name=开场,define)
                琴 e(record=开场完成)
                琴 q(required)
                segment(end,record=开场完成)
                """] },
            Actions = [
                new() { Name = "准备", Character = "琴", Action = "call(开场,once=battle,required),attack(0.1)" },
                new() { Name = "输出", Character = "琴", Action = "attack(1)", Condition = new() { Expression = "record-exists(开场完成)" } }
            ]
        };
        var error = Assert.Throws<FormatException>(() => new JsonCombatFlowExecution(strategy, new PriorityGame()));
        Assert.Contains("开场完成", error.Message);
        Assert.Contains("JSON.info.declarations", error.Message);
    }

    [Fact]
    public async Task LoopPrioritySeesItsUpcomingFirstRoundWithoutAdvancingOnPolling()
    {
        var game = new PriorityGame();
        using var execution = new JsonCombatFlowExecution(new() { Actions = [new()
        {
            Name = "奇数根", Character = "琴", Action = "strategy(loop=battle),attack(0.1,record=已执行)",
            Condition = new() { Expression = "round-odd()" }
        }] }, game, clock: new FakeTimeProvider());
        await execution.StepAsync();
        Assert.Single(game.Actions);
        for (var i = 0; i < 5; i++) await execution.StepAsync();
        Assert.Single(game.Actions); // 轮询自身不推进到第三轮。
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void JsonRoundExpressionsRequireAnExplicitRootLoop(bool loop, bool priorityCondition)
    {
        var action = new JsonAction { Character = "琴", Name = "动作",
            Action = (loop ? "strategy(loop=battle)," : "") +
                (priorityCondition ? "attack(0.1,record=动作完成)" : "round(odd),attack(0.1,record=动作完成)"),
            Condition = new() { Expression = priorityCondition ? "round-odd()" : "" } };
        if (loop) { using var valid = new JsonCombatFlowExecution(new() { Actions = [action] }, new PriorityGame()); }
        else
        {
            var error = Assert.Throws<FormatException>(() => new JsonCombatFlowExecution(new() { Actions = [action] }, new PriorityGame()));
            Assert.Contains("loop=battle", error.Message);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryJsonPriorityConditionValidatesItsRecordReferencesAtItsOwnSource(bool extraPriority)
    {
        var action = new JsonAction { Name = "动作", Character = "琴", Action = "attack(0.1,record=已执行)" };
        if (extraPriority) action.MorePriorities.Add(new() { Expression = "record-exists(拼错记录)", Priority = 0 });
        else action.Condition.Expression = "record-exists(拼错记录)";
        var error = Assert.Throws<FormatException>(() => new JsonCombatFlowExecution(new() { Actions = [action] }, new PriorityGame()));
        Assert.Contains("JSON.actions[0]", error.Message);
        Assert.Contains(extraPriority ? "morePriorities[0]" : "condition", error.Message);
        Assert.Contains("拼错记录", error.Message);
    }

    [Fact]
    public async Task TheNextPriorityDecisionUsesANewObservationFrame()
    {
        var strategy = new JsonCombatStrategy
        {
            Actions =
            [
                new() { Name = "治疗", Index = 0, Character = "琴", Action = "wait(0,record=治疗完成)",
                    Condition = new() { Expression = "low-hp(琴)" } },
                new() { Name = "战斗", Index = 1, Character = "琴", Action = "attack(0.1,record=输出)" }
            ]
        };
        var game = new FrameGame();
        using var execution = new JsonCombatFlowExecution(strategy, game, clock: new FakeTimeProvider());
        for (var i = 0; i < 10 && execution.Context.Find("治疗完成") == null; i++) await execution.StepAsync();
        Assert.NotNull(execution.Context.Find("治疗完成"));
    }

    [Fact]
    public async Task RootCompletionIsCommittedEvenWhenItsActionMakesItsOwnConditionFalse()
    {
        var strategy = new JsonCombatStrategy
        {
            Actions =
            [
                new() { Name = "起手", Index = 1, Character = "琴", Action = "attack(0.1,record=已起手)",
                    Condition = new() { Expression = "!record-exists(已起手)" } },
                new() { Name = "后续", Index = 2, Character = "琴", Action = "wait(0,record=后续完成)",
                    Condition = new() { Expression = "count(起手)=1" } }
            ]
        };
        var game = new PriorityGame();
        using var execution = new JsonCombatFlowExecution(strategy, game, clock: new FakeTimeProvider());
        for (var i = 0; i < 10 && game.Actions.Count < 2; i++) await execution.StepAsync();
        Assert.NotNull(execution.Context.Find("后续完成"));
    }

    [Fact]
    public async Task JsonHistoryUsesConfirmedRootCompletionAndPreservesNeverExecutedSince()
    {
        var strategy = new JsonCombatStrategy
        {
            Actions =
            [
                new() { Name = "起手", Index = 1, Character = "琴", Action = "attack(0.1,record=已起手)",
                    Condition = new() { Expression = "count()=0 && since()>1" } },
                new() { Name = "后续", Index = 2, Character = "琴", Action = "wait(0,record=后续完成)",
                    Condition = new() { Expression = "count(起手)=1 && since(1)<1" } }
            ]
        };
        var game = new PriorityGame();
        using var execution = new JsonCombatFlowExecution(strategy, game, clock: new FakeTimeProvider());
        for (var i = 0; i < 10 && game.Actions.Count < 2; i++) await execution.StepAsync();
        Assert.Equal(new[] { Method.Attack, Method.Wait }, game.Actions);
    }

    [Theory]
    [InlineData("")]
    [InlineData("record-exists(开场完成) || true")]
    public void AnotherJsonRootCannotBypassTheRequiredOpening(string condition)
    {
        var strategy = new JsonCombatStrategy
        {
            Info = new() { Declarations = ["segment(start,name=开场,define)\n琴 e(required)\nsegment(end,record=开场完成)"] },
            Actions =
            [
                new() { Character = "琴", Action = "call(开场,once=battle,required),attack(0.1)" },
                new() { Character = "琴", Action = "attack(0.1)", Condition = new() { Expression = condition } }
            ]
        };
        Assert.Throws<FormatException>(() => new JsonCombatFlowExecution(strategy, new PriorityGame()));
    }

    [Fact]
    public async Task HigherPriorityRootRunsAtTheRootBoundaryWithoutInterruptingAChildOrLosingOpeningState()
    {
        var strategy = new JsonCombatStrategy
        {
            Info = new()
            {
                Declarations = ["""
                    segment(start,name=开场,define)
                    琴 e(required)
                    segment(end,record=开场完成)
                    segment(start,name=主轴,define)
                    琴 attack(0.1)
                    琴 attack(0.1)
                    segment(end)
                    """]
            },
            Actions =
            [
                new() { Name = "治疗", Index = 0, Character = "琴", Action = "wait(0,record=治疗完成)",
                    Condition = new() { Expression = "record-exists(开场完成) && low-hp(琴)" } },
                new() { Name = "战斗", Index = 10, Character = "琴",
                    Action = "strategy(loop=battle),call(开场,once=battle,required),call(主轴)" }
            ]
        };
        var game = new PriorityGame();
        using var execution = new JsonCombatFlowExecution(strategy, game, clock: new FakeTimeProvider());
        for (var i = 0; i < 20 && game.Actions.Count < 4; i++) await execution.StepAsync();
        Assert.Equal(new[] { Method.Skill, Method.Attack, Method.Attack, Method.Wait }, game.Actions);
        Assert.NotNull(execution.Context.Find("开场完成"));
        Assert.NotNull(execution.Context.Find("治疗完成"));
        Assert.Equal(1, game.Actions.Count(method => method == Method.Skill));
    }

    private sealed class FrameGame : ICombatFlowGame
    {
        private bool _low;
        private bool? _cachedLow;
        public void BeginStep() => _cachedLow = null;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            _low = true;
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) =>
            function == "low-hp" ? _cachedLow ??= _low : null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class PriorityGame : ICombatFlowGame
    {
        public bool? BurstReady { get; set; }
        public List<Method> Actions { get; } = [];
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command.Method);
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) =>
            function == "q-ready" ? BurstReady : function == "low-hp" ? Actions.Contains(Method.Attack) : null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
