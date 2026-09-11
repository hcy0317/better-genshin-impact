using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatRecoveryAdmissionTests
{
    [Fact]
    public async Task JsonRootUsesTheSamePhysicalResetPath()
    {
        var clock = new FakeTimeProvider();
        var game = new RecoveryReplayGame(clock, 1);
        using var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Info = new() { Declarations = ["timing(离线护盾,cd=12,duration=20)"] },
            Actions = [new() { Character = "钟离", Action = "strategy(loop=battle),e(hold,wait,required,timing=离线护盾,record=护盾,maintain=护盾),attack(0.6,keep=护盾)" }]
        }, game, clock: clock)!;
        for (var i = 0; i < 240 && game.OutputsAfterRecovery == 0; i++) await runner.StepAsync(default);
        Assert.Equal(1, game.Resets);
        Assert.True(game.OutputsAfterRecovery > 0);
    }

    [Fact]
    public async Task WatchRecoveryCannotBypassAnExpiredAncestorOrRequiredOpening()
    {
        var clock = new FakeTimeProvider();
        var game = new RecoveryReplayGame(clock, 2);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            timing(盾,cd=12,duration=20)
            call(有限,once=battle,required)
            琴 attack(0.6,keep=盾)
            segment(有限,define,timeout=2) {
                钟离 e(required,timing=盾,record=盾,maintain=盾,watch=盾,watch-mode=call,watch-target=补盾,before=19)
                琴 attack(0.1,keep=盾)
            }
            segment(补盾,define) { 钟离 e(required,timing=盾,record=盾,maintain=盾) }
            """), game, clock);
        for (var i = 0; i < 40; i++) await execution.StepAsync();
        Assert.True(game.ExpiredInputCreated);
        Assert.Equal(0, game.Resets);
        Assert.Equal(0, game.OutputsAfterRecovery);
        Assert.True(execution.Context.Find("盾")!.OccurredAt < game.ExpiredInputAt);
    }
    [Theory]
    [InlineData("wrong-battle")]
    [InlineData("old-frame")]
    [InlineData("not-expired")]
    [InlineData("too-soon")]
    [InlineData("cooldown")]
    [InlineData("nan")]
    [InlineData("closed")]
    public void InvalidReadinessEvidenceNeverReleasesThePhysicalRequest(string condition)
    {
        var battle = Guid.NewGuid();
        using var slots = new CombatSkillAttempts(battle);
        slots.TryBegin("钟离", Method.Skill, "source", 0, 3);
        slots.Observe("钟离", Method.Skill, new(battle, 10, 1, null, null));
        var first = new CombatSkillObservation(battle, 11, 4, false, true);
        var second = new CombatSkillObservation(battle, 12, 4.25, false, true);
        switch (condition)
        {
            case "wrong-battle": first = first with { BattleId = Guid.NewGuid() }; break;
            case "old-frame": first = first with { FrameId = 10 }; break;
            case "not-expired": first = first with { CapturedAt = 2 }; break;
            case "too-soon": second = second with { CapturedAt = 4.1 }; break;
            case "cooldown": second = second with { CoolingDown = true }; break;
            case "nan": second = second with { CapturedAt = double.NaN }; break;
            case "closed": slots.Dispose(); break;
        }
        Assert.Null(slots.TryReleaseExpiredReady("钟离", Method.Skill, first, second));
        Assert.True(slots.IsOccupied("钟离", Method.Skill));
    }

    [Fact]
    public async Task ARecoveryProbeCannotExtendItsOwnDeadline()
    {
        var clock = new FakeTimeProvider();
        var game = new RecoveryReplayGame(clock, 1) { ProbeSeconds = 4 };
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            timing(盾,cd=12,duration=20)
            钟离 e(required,maintain=盾,record=盾,timing=盾)
            琴 attack(0.6,keep=盾)
            """), game, clock);
        for (var i = 0; i < 40; i++) await execution.StepAsync();
        Assert.Null(execution.Context.Find("盾"));
        Assert.Equal(0, game.OutputsAfterRecovery);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeCollectorFacadeRecoversFreshEvidenceButStillStopsPersistentUnknown(bool ready)
    {
        var clock = new FakeTimeProvider();
        var game = new RecoveryReplayGame(clock, 2) { ReadyEvidence = ready };
        using var runner = NativeCombatFlowRunner.Create(CombatFlowProgram.Compile(CollectorStrategy), game, clock);
        var stopped = false;
        for (var i = 0; i < 240 && game.OutputsAfterRecovery == 0; i++)
        {
            var step = await runner.StepAsync(default);
            if (!ready && game.ExpiredInputCreated && step.RoundCompleted && step.Result == CombatFlowResult.Failed)
            {
                clock.Advance(TimeSpan.FromSeconds(16));
                await Assert.ThrowsAsync<CombatNotFinishedException>(async () => await runner.StepAsync(default));
                stopped = true;
                break;
            }
        }
        Assert.Equal(!ready, stopped);
        Assert.Equal(ready ? 1 : 0, game.Resets);
        Assert.Equal(ready, game.OutputsAfterRecovery > 0);
    }
    private const string CollectorStrategy = """
        strategy(loop=battle)
        timing(离线护盾,cd=12,duration=20)
        call(开场,once=battle,required)
        钟离 e(hold,wait,timing=离线护盾,record=护盾,maintain=护盾,watch=护盾,watch-mode=call,watch-target=补盾,before=4,required)
        琴 q(if=q-ready(琴))
        纳西妲 e(hold,fast,record=草标记,maintain=草标记,before=2)
        call(轻聚怪,if=e-ready(枫原万叶))
        琴 attack(0.6,keep=护盾), check
        segment(开场,define,record=开场完成) {
            钟离 e(hold,wait,timing=离线护盾,required,record=护盾)
        }
        segment(补盾,define) {
            钟离 e(hold,wait,timing=离线护盾,required,record=护盾,maintain=护盾)
        }
        segment(轻聚怪,define,atomic,requires=record-active(护盾)) {
            枫原万叶 e(hold,fast,required,keep=护盾), attack(0.35), wait(0.4)
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RootAndCollectorWatchCanResumeOnlyAfterFreshPhysicalReadiness(bool collector)
    {
        var clock = new FakeTimeProvider();
        var game = new RecoveryReplayGame(clock, collector ? 2 : 1);
        var script = collector ? CollectorStrategy : """
            strategy(loop=battle)
            timing(离线护盾,cd=12,duration=20)
            钟离 e(hold,wait,required,timing=离线护盾,record=护盾,maintain=护盾)
            琴 attack(0.6,keep=护盾)
            """;
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile(script), game, clock);
        for (var i = 0; i < 240 && game.OutputsAfterRecovery == 0; i++) await execution.StepAsync();
        Assert.True(game.ExpiredInputCreated);
        Assert.Equal(1, game.Resets);
        Assert.True(game.OutputsAfterRecovery > 0);
        Assert.True(execution.Context.Find("护盾")!.OccurredAt > game.ExpiredInputAt);
    }

    private sealed class RecoveryReplayGame(FakeTimeProvider clock, int failShieldInput) : ICombatFlowGame
    {
        private CombatSkillAttempts? _slots;
        private int _shieldInputs;
        private long _frame;
        public bool ExpiredInputCreated { get; private set; }
        public double ExpiredInputAt { get; private set; }
        public int Resets { get; private set; }
        public int OutputsAfterRecovery { get; private set; }
        public bool ReadyEvidence { get; init; } = true;
        public double ProbeSeconds { get; init; } = .25;
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => function switch
        { "e-ready" => true, "q-ready" => false, "low-hp" => false, _ => null };
        public ValueTask YieldAsync(CancellationToken ct) { clock.Advance(TimeSpan.FromSeconds(.25)); return ValueTask.CompletedTask; }
        public ValueTask WaitAfterFailedPassAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromSeconds(1)); return ValueTask.CompletedTask; }
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Failed);
            if (action.Command.Name == "钟离" && action.Command.Method == Method.Skill && ++_shieldInputs == failShieldInput)
            {
                _slots = new(action.BattleId);
                _slots.TryBegin("钟离", Method.Skill, action.CommandId, action.Now, action.Now + action.RemainingBudget);
                ExpiredInputAt = action.Now;
                ExpiredInputCreated = true;
                clock.Advance(TimeSpan.FromSeconds(16)); // replay: confirmation window consumed without a usable CD sample
                return ValueTask.FromResult(CombatFlowResult.Pending);
            }
            if (Resets > 0 && action.Command.Method == Method.Attack && action.Command.Options.GetValueOrDefault("keep") == "护盾") OutputsAfterRecovery++;
            clock.Advance(TimeSpan.FromSeconds(1));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask<CombatSkillAttempt?> TryRecoverExpiredSkillAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (!ReadyEvidence) return ValueTask.FromResult<CombatSkillAttempt?>(null);
            var first = new CombatSkillObservation(action.BattleId, ++_frame, action.Now, false, true);
            clock.Advance(TimeSpan.FromSeconds(ProbeSeconds));
            var second = new CombatSkillObservation(action.BattleId, ++_frame, action.Now, false, true);
            var reset = _slots?.TryReleaseExpiredReady(action.Command.Name, action.Command.Method, first, second);
            if (reset != null) Resets++;
            return ValueTask.FromResult(reset);
        }
    }
    [Fact]
    public void GoalRecoveryRequiresExhaustionAndConsumesEachReceiptOnceAcrossGoals()
    {
        var episodes = new CombatFlowEpisodes();
        var proof = Guid.NewGuid();
        Assert.True(episodes.TrySpend("shield", 0, 15, 3, out _));
        Assert.False(episodes.TryReopenAfterSkillReset("shield", proof, 1));
        Assert.True(episodes.CanTrySkillReset("shield", 16));
        Assert.True(episodes.TryReopenAfterSkillReset("shield", proof, 16));
        Assert.True(episodes.TrySpend("shield", 16, 15, 3, out var deadline));
        Assert.Equal(31, deadline);
        Assert.True(episodes.TrySpend("other", 0, 15, 3, out _));
        Assert.False(episodes.TryReopenAfterSkillReset("other", proof, 32));
        Assert.True(episodes.TryReopenAfterSkillReset("shield", Guid.NewGuid(), 32));
        episodes.Resolve("shield");
        Assert.True(episodes.TrySpend("shield", 32, 15, 3, out _));
        Assert.False(episodes.CanTrySkillReset("shield", 48));
        Assert.False(episodes.TryReopenAfterSkillReset("shield", Guid.NewGuid(), 48));
    }
    [Fact]
    public void ExpiredPhysicalRequestNeedsTwoFreshReadyFramesAndCanBeReleasedOnlyOnce()
    {
        var battle = Guid.NewGuid();
        using var slots = new CombatSkillAttempts(battle);
        var attempt = slots.TryBegin("钟离", Method.Skill, "source", 0, 3)!;
        var first = new CombatSkillObservation(battle, 10, 4, false, true);
        var second = new CombatSkillObservation(battle, 11, 4.25, false, true);
        Assert.Null(slots.TryReleaseExpiredReady("钟离", Method.Skill, first, first));
        Assert.Null(slots.TryReleaseExpiredReady("钟离", Method.Skill, first, second with { Ready = null }));
        Assert.Same(attempt, slots.TryReleaseExpiredReady("钟离", Method.Skill, first, second));
        Assert.False(slots.IsOccupied("钟离", Method.Skill));
        Assert.Null(slots.TryReleaseExpiredReady("钟离", Method.Skill, first, second));
        Assert.Null(slots.TakeConfirmation("钟离", Method.Skill, "source", 4.25));
    }
}
