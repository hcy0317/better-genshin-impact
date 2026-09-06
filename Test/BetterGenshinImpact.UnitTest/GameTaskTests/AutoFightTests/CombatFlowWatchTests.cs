using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowWatchTests
{
    [Fact]
    public async Task NewProducerEvidenceCanReopenADeferredWindowAndAdmitJointOutput()
    {
        var catalog = new SkillCatalogSnapshot(new Dictionary<string, SkillFact>
        { ["test.a"] = Fact("test.a", "钟离", "shield"), ["test.b"] = Fact("test.b", "班尼特", "buff") });
        var program = CombatFlowProgram.Compile("""
            strategy(loop=battle)
            钟离 e(record=甲,maintain=甲,watch=甲,before=1,required)
            班尼特 e(record=乙,maintain=乙,watch=乙,before=1)
            segment(联合,atomic,record=联合完成) {
                琴 attack(1,keep=甲,required), attack(1,keep=乙,required)
            }
            琴 attack(0.1,keep=甲)
            """, catalog);
        var clock = new FakeTimeProvider();
        var game = new TimedGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        for (var step = 0; step < 20 && execution.LastMaintenanceDecision == null; step++) await execution.StepAsync();
        Assert.NotNull(execution.LastMaintenanceDecision);
        Assert.Null(execution.Context.Find("联合完成"));
        var old = execution.Context.Find("乙")!;
        Assert.True(execution.Context.TryRecord("乙", execution.Context.Now, old.Duration, old.Source));
        for (var step = 0; step < 10 && execution.Context.Find("联合完成") == null; step++) await execution.StepAsync();
        Assert.NotNull(execution.Context.Find("联合完成"));
    }

    [Fact]
    public async Task FallbackProgressDoesNotRearmTheSameUnsolvedConflictingWindow()
    {
        var catalog = new SkillCatalogSnapshot(new Dictionary<string, SkillFact>
        { ["test.a"] = Fact("test.a", "钟离", "shield"), ["test.b"] = Fact("test.b", "班尼特", "buff") });
        var program = CombatFlowProgram.Compile("""
            strategy(loop=battle)
            钟离 e(record=甲,maintain=甲,watch=甲,before=1,required)
            班尼特 e(record=乙,maintain=乙,watch=乙,before=1)
            segment(start,atomic)
            琴 attack(1,keep=甲,required), attack(1,keep=乙,required)
            segment(end)
            琴 attack(0.1,keep=甲)
            """, catalog);
        var clock = new FakeTimeProvider();
        var game = new TimedGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        for (var step = 0; step < 500 && execution.Context.Now < 30; step++) await execution.StepAsync();
        Assert.InRange(game.Actions.Count(command => command.Name == "班尼特" && command.Method == Method.Skill), 1, 3);
        Assert.True(game.Actions.Count(command => command.Method == Method.Attack) >= 20);
    }

    [Fact]
    public async Task AReusedFirstMaintenancePointStillArmsItsWatchWithoutRecasting()
    {
        var program = CombatFlowProgram.Compile("""
            strategy(loop=battle)
            timing(窗口,duration=20)
            record(覆盖,duration=20)
            钟离 e(timing=窗口,record=覆盖,maintain=覆盖,watch=覆盖,before=4,watch-mode=call,watch-target=续期)
            琴 wait(0.1)
            琴 attack(1,keep=覆盖)
            segment(start,name=续期,define)
            钟离 e(timing=窗口,record=覆盖,required)
            segment(end)
            """);
        var clock = new FakeTimeProvider();
        var game = new TimedGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.StepAsync();
        var original = execution.Context.Find("覆盖");
        Assert.Equal(Method.Wait, Assert.Single(game.Actions).Method);
        Assert.Equal(0, original!.OccurredAt);
        clock.Advance(TimeSpan.FromSeconds(16));
        await execution.StepAsync();
        Assert.Equal(Method.Skill, game.Actions.Last().Method);
        Assert.True(execution.Context.Find("覆盖")!.Generation > original.Generation);
    }

    [Fact]
    public async Task AnExpiredNonLoopWatchWithoutASuccessorUsesItsDeclaredRecovery()
    {
        var program = CombatFlowProgram.Compile("""
            timing(窗口,duration=3)
            segment(start,onfail=保底)
            钟离 e(hold,wait,refresh,timing=窗口)
            琴 wait(4)
            琴 attack(1)
            segment(end)
            segment(start,name=保底,define)
            琴 attack(0.1,record=已恢复)
            segment(end)
            """);
        var clock = new FakeTimeProvider();
        var game = new TimedGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.RunRoundAsync();
        Assert.NotNull(execution.Context.Find("已恢复"));
        Assert.DoesNotContain(game.Actions, action => action.Method == Method.Attack && action.Args![0] == "1");
    }

    [Fact]
    public async Task ConflictingMaintenanceWindowsPreserveSurvivalAndReachTheDeclaredFallback()
    {
        var catalog = new SkillCatalogSnapshot(new Dictionary<string, SkillFact>
        {
            ["test.a"] = Fact("test.a", "钟离", "shield"),
            ["test.b"] = Fact("test.b", "班尼特", "buff")
        });
        var program = CombatFlowProgram.Compile("""
            strategy(loop=battle)
            钟离 e(record=甲,maintain=甲,watch=甲,before=1,required)
            班尼特 e(record=乙,maintain=乙,watch=乙,before=1)
            segment(start,atomic,timeout=8)
            琴 attack(1,keep=甲,required)
            琴 attack(1,keep=乙,required)
            segment(end,record=联合输出)
            琴 attack(0.1,keep=甲,record=保底)
            """, catalog);
        var clock = new FakeTimeProvider();
        var game = new TimedGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        for (var i = 0; i < 20 && execution.Context.Find("保底") == null; i++) await execution.StepAsync();
        Assert.NotNull(execution.Context.Find("保底"));
        Assert.True(execution.Context.Remaining("甲") > 0);
        Assert.Null(execution.Context.Find("联合输出"));
        Assert.DoesNotContain(game.Actions, action => action.Method == Method.Attack && action.Args![0] == "1");
        Assert.InRange(game.Actions.Count, 3, 12);
    }

    private static SkillFact Fact(string id, string actor, string capability) => new()
    {
        Id = id, Character = actor, Slot = "e", Metrics = new() { ["duration"] = new() { Values = [5] } },
        Forms = new() { ["press"] = new() { Effects = [new() { Id = capability, Capability = capability, DurationMetric = "duration" }] } }
    };

    private sealed class TimedGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public List<CombatCommand> Actions { get; } = [];
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command);
            clock.Advance(TimeSpan.FromSeconds(action.Command.Method == Method.Skill ? 1 :
                double.Parse(action.Command.Args![0], System.Globalization.CultureInfo.InvariantCulture)));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
