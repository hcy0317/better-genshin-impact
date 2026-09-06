using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatFlowAtomicTests
{
    [Fact]
    public async Task SlowNestedWorkRechecksCoverageUntilTheOutermostRequiredAtomicEnd()
    {
        var program = CombatFlowProgram.Compile("""
            record(窗口,duration=10)
            segment(外层,atomic,timeout=20,record=外层完成) {
                segment(内层,atomic,required,timeout=12) {
                    琴 wait(0.1)
                    琴 attack(1,keep=窗口,required)
                }
                琴 attack(1,keep=窗口,required)
            }
            琴 attack(0.1,record=保底)
            """);
        var clock = new FakeTimeProvider();
        var game = new TimedGame(clock) { FirstWaitSeconds = 7 };
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.RunRoundAsync();
        Assert.DoesNotContain(game.Actions, command => command.Method == Method.Attack && command.Args![0] == "1");
        Assert.Null(execution.Context.Find("外层完成"));
        Assert.NotNull(execution.Context.Find("保底"));
    }

    [Fact]
    public async Task AnOuterAtomicRequirementStillProtectsAHeldActionInsideACalledChild()
    {
        var catalog = new SkillCatalogSnapshot(new Dictionary<string, SkillFact> { ["test.e"] = new()
        {
            Id = "test.e", Character = "钟离", Slot = "e", Metrics = new() { ["duration"] = new() { Values = [20] } },
            Forms = new() { ["press"] = new() { Effects = [new() { Id = "shield", Capability = "shield", DurationMetric = "duration" }] } }
        } });
        var program = CombatFlowProgram.Compile("""
            钟离 e(required,record=覆盖)
            segment(外层,atomic,requires=record-active(覆盖),record=外层完成) {
                call(子动作,required)
            }
            segment(子动作,define,record=子动作完成) { 琴 attack(1) }
            """, catalog);
        var clock = new FakeTimeProvider();
        var game = new RevokingGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        game.Context = execution.Context;
        await execution.RunRoundAsync();
        Assert.True(game.Released);
        Assert.InRange(game.StoppedAt, .25, .30);
        Assert.Null(execution.Context.Find("子动作完成"));
        Assert.Null(execution.Context.Find("外层完成"));
    }

    [Fact]
    public async Task ExpiryBetweenMacroCommandsReleasesHeldInputBeforeEnteringRecovery()
    {
        var program = CombatFlowProgram.Compile("""
            record(覆盖,duration=20)
            segment(start,atomic,onfail=保底,requires=record-active(覆盖))
            琴 keydown(VK_LBUTTON,keep=覆盖,required), wait(0.5,required), keyup(VK_LBUTTON)
            segment(end,record=宏完成)
            segment(start,name=保底,define)
            琴 attack(0.1)
            segment(end)
            """);
        var clock = new FakeTimeProvider();
        var game = new ExpiringMacroGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.RunRoundAsync();
        Assert.True(game.RecoveryRan);
        Assert.False(game.HeldDuringRecovery);
        Assert.False(game.Held);
        Assert.Null(execution.Context.Find("宏完成"));
    }

    [Fact]
    public async Task ConfirmedRevocationInterruptsAHeldAtomicActionAtTheNextWaitSlice()
    {
        var catalog = new SkillCatalogSnapshot(new Dictionary<string, SkillFact> { ["test.e"] = new()
        {
            Id = "test.e", Character = "钟离", Slot = "e", Metrics = new() { ["duration"] = new() { Values = [20] } },
            Forms = new() { ["press"] = new() { Effects = [new() { Id = "shield", Capability = "shield", DurationMetric = "duration" }] } }
        } });
        var program = CombatFlowProgram.Compile("""
            钟离 e(required,record=覆盖)
            segment(start,atomic,timeout=8)
            琴 attack(1,keep=覆盖,required,record=持续动作完成)
            segment(end,record=原子成功)
            """, catalog);
        var clock = new FakeTimeProvider();
        var game = new RevokingGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        game.Context = execution.Context;
        await execution.RunRoundAsync();
        Assert.True(game.Released);
        Assert.InRange(game.StoppedAt, 0.25, 0.30);
        Assert.Null(execution.Context.Find("持续动作完成"));
        Assert.Null(execution.Context.Find("原子成功"));
    }

    [Fact]
    public async Task AnAtomicStateRequirementMustRemainValidForTheWholeSegment()
    {
        var program = CombatFlowProgram.Compile("""
            record(有限窗口,duration=2.5)
            segment(start,atomic,timeout=8,requires=record-active(有限窗口))
            琴 attack(1)
            琴 attack(1)
            segment(end,record=原子成功)
            琴 attack(0.1,record=保底)
            """);
        var clock = new FakeTimeProvider();
        var game = new TimedGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.RunRoundAsync();
        Assert.Equal(new[] { "0.1" }, game.Actions.Select(action => action.Args![0]));
        Assert.Null(execution.Context.Find("原子成功"));
    }

    [Fact]
    public async Task AWindowCreatedInsideAtomicMustCoverAllItsDependentOutput()
    {
        var program = CombatFlowProgram.Compile("""
            timing(短窗口,duration=2.5)
            segment(start,atomic,timeout=8)
            琴 e(required,record=新窗口,timing=短窗口)
            琴 attack(1,keep=新窗口,required)
            琴 attack(1,keep=新窗口,required)
            segment(end,record=原子成功)
            琴 attack(0.1,record=保底)
            """);
        var clock = new FakeTimeProvider();
        var game = new TimedGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.RunRoundAsync();
        Assert.Equal(new[] { "0.1" }, game.Actions.Where(action => action.Method == Method.Attack).Select(action => action.Args![0]));
        Assert.NotNull(execution.Context.Find("新窗口"));
        Assert.Null(execution.Context.Find("原子成功"));
    }

    [Fact]
    public async Task ASelectedDynamicBranchNeedsCoverageForItsWholeAtomicRemainder()
    {
        var program = CombatFlowProgram.Compile("""
            record(有限窗口,duration=2.5)
            segment(start,atomic,timeout=8)
            branch(if=q-ready(琴),then=备用,else=窗口输出,required)
            segment(end,record=原子成功)
            琴 attack(0.1,record=保底)
            segment(start,name=窗口输出,define)
            琴 attack(1,keep=有限窗口,required)
            琴 attack(1,keep=有限窗口,required)
            segment(end)
            segment(start,name=备用,define)
            琴 wait(0.1)
            segment(end)
            """);
        var clock = new FakeTimeProvider();
        var game = new TimedGame(clock);
        using var execution = new CombatFlowExecution(program, game, clock);
        await execution.RunRoundAsync();
        Assert.Equal(new[] { "0.1" }, game.Actions.Select(action => action.Args![0]));
        Assert.Null(execution.Context.Find("原子成功"));
        Assert.NotNull(execution.Context.Find("保底"));
    }

    private sealed class ExpiringMacroGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public bool Held { get; private set; }
        public bool HeldDuringRecovery { get; private set; }
        public bool RecoveryRan { get; private set; }
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            if (action.Command.Method == Method.KeyDown) { Held = true; clock.Advance(TimeSpan.FromSeconds(21)); }
            if (action.Command.Method == Method.KeyUp) Held = false;
            if (action.Command.Method == Method.Attack) { RecoveryRan = true; HeldDuringRecovery = Held; }
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public void ReleaseHeldInput() => Held = false;
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class TimedGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public double? FirstWaitSeconds { get; set; }
        public List<CombatCommand> Actions { get; } = [];
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            Actions.Add(action.Command);
            if (action.Command.Method == Method.Attack || action.Command.Method == Method.Wait)
            {
                var seconds = double.Parse(action.Command.Args![0], System.Globalization.CultureInfo.InvariantCulture);
                if (action.Command.Method == Method.Wait && FirstWaitSeconds is { } delayed) { seconds = delayed; FirstWaitSeconds = null; }
                clock.Advance(TimeSpan.FromSeconds(seconds));
            }
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => function == "q-ready" ? false : null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class RevokingGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public CombatFlowContext Context { get; set; } = null!;
        public bool Released { get; private set; }
        public double StoppedAt { get; private set; }
        public async ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return CombatFlowResult.Skipped;
            if (action.Command.Method != Method.Attack) return CombatFlowResult.Succeeded;
            using var scope = new CombatActionScope(action, ct);
            try
            {
                await scope.WaitAsync(1000, (milliseconds, _) =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
                    if (Context.Now >= 0.25 && Context.Remaining("覆盖") > 0)
                    {
                        var effect = Context.Find("覆盖")!;
                        Context.InvalidateEffect(effect.EffectInstanceId!.Value, effect.EffectVersion, "模拟已确认失效");
                    }
                    return Task.CompletedTask;
                });
                return CombatFlowResult.Succeeded;
            }
            catch (CombatActionInterruptedException) { return CombatFlowResult.Skipped; }
            finally { Released = true; StoppedAt = Context.Now; }
        }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
