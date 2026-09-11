using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatPendingContinuationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingConfirmationCannotOutliveAnAncestorRequirement(bool atomic)
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock);
        var script = "segment(外层,requires=in-party(钟离)" + (atomic ? ",atomic" : "") + ") {\n" +
            "segment(内层) {\n琴 q(required,record=原Q)\n}\n}";
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile(script), game, clock);
        await execution.StepAsync();
        Assert.True(execution.HasPendingConfirmation);
        game.RequirementActive = false;
        await execution.RunRoundAsync();
        Assert.Null(execution.Context.Find("原Q"));
        Assert.False(execution.HasPendingConfirmation);
        Assert.Equal(1, game.SkillInputs);
        Assert.Single(game.Actors);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("dispose")]
    [InlineData("cancelled")]
    [InlineData("exception")]
    [InlineData("displaced")]
    [InlineData("frame-failed")]
    public async Task PendingEndKeepsOriginalIdentityWithoutInputCreditOrReplacingExceptions(string ending)
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock) { Unknown = true };
        var script = ending switch
        {
            "displaced" => """
                timing(窗口,duration=20)
                record(覆盖,duration=20)
                钟离 e(timing=窗口,record=覆盖,maintain=覆盖,watch=覆盖,before=18)
                琴 q(required,record=原Q)
                钟离 e(timing=窗口,record=覆盖,maintain=覆盖,watch=覆盖,before=18)
                """,
            "frame-failed" => """
                record(范围,duration=20)
                segment(输出,requires=record-active(范围)) {
                    琴 q(required,record=原Q)
                }
                """,
            _ => "琴 q(required,record=原Q)"
        };
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile(script), game, clock);
        await execution.StepAsync();
        var original = game.OriginalAttempt!;
        Assert.NotNull(original);
        if (ending is "cancelled" or "exception")
        {
            var expected = ending == "cancelled" ? (Exception)new OperationCanceledException("原始取消")
                : new InvalidOperationException("原始截图失败");
            game.ConfirmationFailure = expected;
            var actual = await Record.ExceptionAsync(async () => await execution.StepAsync());
            Assert.Same(expected, actual);
        }
        else if (ending == "displaced")
        {
            await execution.StepAsync();
            await execution.StepAsync();
        }
        else if (ending != "dispose")
        {
            clock.Advance(TimeSpan.FromSeconds(ending == "expired" ? 6 : 20));
            await execution.StepAsync();
        }
        var actionCalls = execution.RuntimeStatistics.GameActionCalls;
        execution.Dispose();
        var end = Assert.Single(execution.RuntimeStatistics.RecentEvents.Where(entry => entry.ReportedResult == "PendingEnded"));
        Assert.Equal(original.AttemptId, end.AttemptId);
        Assert.Equal(original.CommandId, end.CommandId);
        Assert.Equal(original.Deadline, end.ConfirmationDeadline);
        Assert.Contains(ending, end.Reason!);
        Assert.False(end.InputStarted);
        Assert.Null(end.EffectiveInputAt);
        Assert.Equal(actionCalls, execution.RuntimeStatistics.GameActionCalls);
        Assert.Equal(game.Actors.Count, execution.RuntimeStatistics.GameActionCalls);
        Assert.Equal(1, game.SkillInputs);
        Assert.Null(execution.Context.Find("原Q"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BothHostsProtectPendingConfirmationUntilCompletionOrOriginalExpiry(bool json, bool expire)
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock);
        using var runner = json
            ? NativeCombatFlowRunner.Create(new JsonCombatStrategy
            {
                Actions = [new() { Character = "琴", Action = "q(required,record=原Q)" }]
            }, game, clock: clock)!
            : NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("琴 q(required,record=原Q)"), game, clock);
        Assert.False(runner.HasPendingConfirmation);
        await runner.StepAsync(default);
        Assert.True(runner.HasPendingConfirmation);
        if (expire) clock.Advance(TimeSpan.FromSeconds(6));
        else await runner.StepAsync(default);
        Assert.False(runner.HasPendingConfirmation);
        runner.Dispose();
        Assert.False(runner.HasPendingConfirmation);
        Assert.Equal(1, game.SkillInputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewPendingGetsOneConfirmationBeforeDueMaintenanceThenMaintenanceResumes(bool unknown)
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock) { Unknown = unknown };
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            timing(窗口,duration=20)
            record(覆盖,duration=20)
            钟离 e(timing=窗口,record=覆盖,maintain=覆盖,watch=覆盖,before=18,watch-mode=call,watch-target=续期)
            琴 q(if=q-ready(琴),required,record=原Q)
            segment(续期,define) {
                钟离 e(timing=窗口,record=覆盖,required)
            }
            """), game, clock);
        Assert.Equal(CombatFlowResult.Pending, (await execution.StepAsync()).Result);
        await execution.StepAsync();
        Assert.Equal(new[] { "琴", "琴" }, game.Actors);
        Assert.Equal(!unknown, execution.Context.Find("原Q") != null);
        await execution.StepAsync();
        Assert.Equal("钟离", game.Actors.Last());
        Assert.Equal(1, game.SkillInputs);
        Assert.False(game.ConfirmationAdmittedInput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NestedOnceOpeningWaitsForItsOriginalConfirmationBeforeCompleting(bool atomic)
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock);
        var script = "call(开场,once=battle,required)\n钟离 attack(0.1)\n" +
            "segment(开场,define,record=开场完成" + (atomic ? ",atomic" : "") + ") {\n琴 q(if=q-ready(琴),required,record=原Q)\n}";
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile(script), game, clock);
        await execution.StepAsync();
        Assert.Null(execution.Context.Find("开场完成"));
        Assert.Equal(CombatFlowResult.Succeeded, await execution.RunRoundAsync());
        Assert.NotNull(execution.Context.Find("开场完成"));
        Assert.Equal(0d, execution.Context.Find("原Q")?.OccurredAt);
        await execution.RunRoundAsync();
        Assert.Equal(1, game.SkillInputs);
    }

    [Fact]
    public async Task ExpiredAncestorRequirementCannotBeBypassedByPendingConfirmation()
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            timing(盾,cd=12,duration=20)
            钟离 e(if=false,record=护盾,timing=盾)
            call(开场,once=battle,required)
            segment(开场,define,atomic,requires=record-active(护盾),record=开场完成) {
                琴 q(if=q-ready(琴),required,record=原Q)
            }
            """), game, clock);
        execution.Context.TryRecord("护盾", 0, 20);
        await execution.StepAsync();
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(CombatFlowResult.Failed, await execution.RunRoundAsync());
        Assert.Null(execution.Context.Find("原Q"));
        Assert.Null(execution.Context.Find("开场完成"));
        Assert.Equal(1, game.SkillInputs);
        Assert.Single(game.Actors);
    }

    [Fact]
    public async Task JsonKeepsItsPendingRootWhenTheEntryConditionTurnsFalse()
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock);
        using var execution = new JsonCombatFlowExecution(new JsonCombatStrategy
        {
            Actions = [
                new() { Character = "琴", Index = 0, Action = "q(required,record=原Q)", Condition = new() { Expression = "q-ready(琴)" } },
                new() { Character = "钟离", Index = 1, Action = "attack(0.1,required)", Condition = new() { Expression = "true" } }
            ]
        }, game, clock: clock);
        await execution.StepAsync();
        Assert.False(execution.IsAtRootBoundary);
        await execution.StepAsync();
        Assert.Equal(0d, execution.Context.Find("原Q")?.OccurredAt);
        Assert.Equal(new[] { "琴", "琴" }, game.Actors);
        Assert.Equal(1, game.SkillInputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownOrWrongActorCannotRenewTheOriginalDeadline(bool wrongActor)
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock) { Unknown = !wrongActor, WrongActor = wrongActor };
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("琴 q(if=q-ready(琴),required,record=原Q)"), game, clock);
        var result = await execution.RunRoundAsync();
        Assert.Equal(CombatFlowResult.Failed, result);
        Assert.Null(execution.Context.Find("原Q"));
        Assert.Equal(1, game.SkillInputs);
        Assert.InRange(execution.Context.Now, 8, 8.3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReleasedOrReplacedSlotCannotTurnConfirmationIntoNewInput(bool replaced)
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock) { ReleaseSlot = !replaced, ReplaceSlot = replaced };
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("琴 q(if=q-ready(琴),required,record=原Q)"), game, clock);
        await execution.RunRoundAsync();
        Assert.Equal(1, game.SkillInputs);
        Assert.False(game.ConfirmationAdmittedInput);
        Assert.Null(execution.Context.Find("原Q"));
    }

    [Fact]
    public async Task CancellationStopsARegisteredPendingRequestWithoutAnotherInput()
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("琴 q(if=q-ready(琴),required)"), game, clock);
        using var ct = new CancellationTokenSource();
        await execution.StepAsync(ct.Token);
        ct.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await execution.StepAsync(ct.Token));
        Assert.Equal(1, game.SkillInputs);
        Assert.Single(game.Actors);
    }

    [Fact]
    public async Task PendingCastIsConfirmedBeforeOtherActionsEvenAfterReadinessTurnsFalse()
    {
        var clock = new FakeTimeProvider();
        var game = new PendingGame(clock);
        using var execution = new CombatFlowExecution(CombatFlowProgram.Compile("""
            琴 q(if=q-ready(琴),required,record=原Q)
            钟离 attack(0.1)
            """), game, clock);
        Assert.Equal(CombatFlowResult.Pending, (await execution.StepAsync()).Result);
        await execution.StepAsync();
        Assert.Equal(0d, execution.Context.Find("原Q")?.OccurredAt);
        Assert.Equal(1, game.SkillInputs);
        Assert.DoesNotContain("钟离", game.Actors);
    }

    private sealed class PendingGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        private CombatSkillAttempts? _attempts;
        private long _frame;
        public int SkillInputs { get; private set; }
        public List<string> Actors { get; } = [];
        public bool Unknown { get; init; }
        public bool WrongActor { get; init; }
        public bool ReleaseSlot { get; init; }
        public bool ReplaceSlot { get; init; }
        public bool ConfirmationAdmittedInput { get; private set; }
        public CombatSkillAttempt? OriginalAttempt { get; private set; }
        public Exception? ConfirmationFailure { get; set; }
        public bool RequirementActive { get; set; } = true;
        public bool HasPendingSkill(CombatFlowAction action) => _attempts?.HasUnresolved(action.Command.Name, action.Command.Method) == true;
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) =>
            function == "q-ready" ? SkillInputs == 0 : function == "in-party" ? RequirementActive : null;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            Actors.Add(action.Command.Name);
            if (action.Command.Method != Method.Burst)
            {
                action.TryBeginInput();
                return ValueTask.FromResult(CombatFlowResult.Succeeded);
            }
            _attempts ??= new(action.BattleId);
            if (action.IsConfirmationOnly)
            {
                if (ConfirmationFailure != null) throw ConfirmationFailure;
                clock.Advance(TimeSpan.FromMilliseconds(250));
                ConfirmationAdmittedInput |= action.TryBeginInput();
                if (ReleaseSlot || ReplaceSlot)
                {
                    _attempts.Dispose();
                    _attempts = new(action.BattleId);
                    if (ReplaceSlot) _attempts.TryBegin(action.Command.Name, action.Command.Method,
                        action.CommandId, action.Now, action.Now + 8);
                }
                return ValueTask.FromResult(NativeCombatFlowRunner.ReconcilePendingSkill(_attempts, action, action.Command.Name,
                    () => NativeCombatFlowRunner.GatePendingSkillObservation(new(action.BattleId, ++_frame, action.Now,
                        Unknown ? null : true, Unknown ? null : false), !WrongActor)) ?? CombatFlowResult.Skipped);
            }
            if (!action.TryBeginInput()) return ValueTask.FromResult(CombatFlowResult.Skipped);
            var attempt = _attempts.TryBegin(action.Command.Name, action.Command.Method, action.CommandId, action.InputAt!.Value, action.AbsoluteDeadline)!;
            Assert.True(action.RegisterPendingAttempt(attempt));
            OriginalAttempt = attempt;
            action.DiagnosticAttemptId = attempt.AttemptId;
            SkillInputs++;
            clock.Advance(TimeSpan.FromSeconds(2));
            return ValueTask.FromResult(CombatFlowResult.Pending);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
}
