using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatRootBoundaryTests
{
    [Fact]
    public async Task CompletedAtomicSetupAndSkippedBurstCannotHideTheNextMeleeAttackFromHostEngagement()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        // A plain root primitive already returns. This control validates the same
        // fake scene, melee demand and real host input path before the failing case.
        var control = await Replay("""
            strategy(loop=battle)
            娜维娅 e(hold,fast,required)
            娜维娅 attack(0.4,required)
            """);
        AssertEngagementBeforeAttack(control, "plain root control");

        // Keep the actual rock-party topology: the atomic call is followed by
        // q(if=q-ready), then the ordinary attack. The Q condition is known false,
        // not unknown preparation, and cannot authorize a burst or renew a budget.
        var actual = await Replay("""
            strategy(loop=battle)
            call(一枪短打,required)
            娜维娅 q(if=q-ready(娜维娅))
            娜维娅 attack(0.4,required)
            segment(一枪短打,define,atomic) {
                娜维娅 e(hold,fast,required)
            }
            """);
        AssertEngagementBeforeAttack(actual, "atomic setup and skipped Q");
        Assert.Null(System.Windows.Application.Current);
    }

    [Fact]
    public async Task AtomicSequenceKeepsHostEngagementOutUntilItsAttackCompletes()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var result = await Replay("""
            strategy(loop=battle)
            call(不可中断,required)
            segment(不可中断,define,atomic) {
                娜维娅 e(hold,fast,required)
                娜维娅 q(if=q-ready(娜维娅))
                娜维娅 attack(0.4,required)
            }
            """);
        Assert.Equal(1, result.SkillInputs);
        Assert.Equal(1, result.AttackInputs);
        Assert.Equal(0, result.BurstInputs);
        Assert.DoesNotContain("camera", result.Events);
        Assert.DoesNotContain("approach", result.Events);
        Assert.Empty(result.Requests);
        Assert.Null(System.Windows.Application.Current);
    }

    private static void AssertEngagementBeforeAttack(Scenario result, string label)
    {
        Assert.Equal(1, result.SkillInputs);
        Assert.Equal(1, result.AttackInputs);
        Assert.Equal(0, result.BurstInputs);
        var skill = Array.IndexOf(result.Events, "skill");
        var camera = Array.IndexOf(result.Events, "camera");
        var attack = Array.IndexOf(result.Events, "attack");
        Assert.True(skill >= 0 && camera > skill && attack > camera,
            $"{label}: expected skill -> host camera -> ordinary attack; actual: {string.Join(" -> ", result.Events)}");
        var decisions = result.Requests.Where(input => input.Kind == CombatBattleHostInputKind.Camera).ToArray();
        Assert.True(decisions.Length >= 2, "Camera needs a successor business frame before native submission.");
        Assert.Equal(decisions[0].RequestId, decisions[1].RequestId);
        Assert.Equal(decisions[0].DeadlineTimestamp, decisions[1].DeadlineTimestamp);
        Assert.True(decisions[1].Source.IsAfter(decisions[0].Source));
    }

    private static async Task<Scenario> Replay(string script)
    {
        using var task = TaskExecutionScope.BeginOwned();
        var clock = new FakeTimeProvider();
        var events = new List<string>();
        var game = new RecordingGame(clock, events);
        using var flow = NativeCombatFlowRunner.Create(CombatFlowProgram.Compile(script), game, clock);
        var io = new EngagementIo(clock, flow.Context.BattleId, game, events);
        using var host = new CombatBattleHost(io, new()
        { FinishDetectionEnabled = false, SeekEnabled = true });
        for (var i = 0; i < 200 && game.AttackInputs == 0; i++)
        {
            var result = await host.AdvanceAsync(flow, default);
            Assert.True(result == CombatBattleHostResult.Continue,
                $"Replay stopped before the attack: {host.Reason}; {string.Join(" -> ", events)}");
        }
        return new(events.ToArray(), io.Requests.ToArray(), game.SkillInputs, game.AttackInputs, game.BurstInputs);
    }

    private sealed record Scenario(string[] Events, CombatBattleHostInput[] Requests,
        int SkillInputs, int AttackInputs, int BurstInputs);

    private sealed class RecordingGame(FakeTimeProvider clock, List<string> events) : ICombatFlowGame
    {
        public int SkillInputs { get; private set; }
        public int AttackInputs { get; private set; }
        public int BurstInputs { get; private set; }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => function != "q-ready";
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(action.TryBeginInput(), "The original action must still admit its input.");
            if (action.Command.Method == Method.Skill) { SkillInputs++; events.Add("skill"); }
            else if (action.Command.Method == Method.Attack) { AttackInputs++; events.Add("attack"); }
            else if (action.Command.Method == Method.Burst) { BurstInputs++; events.Add("burst"); }
            else throw new InvalidOperationException($"Unexpected primitive {action.Command.Method}");
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            clock.Advance(TimeSpan.FromMilliseconds(50));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class EngagementIo(FakeTimeProvider clock, Guid battleId, RecordingGame game,
        List<string> events) : ICombatBattleHostIo
    {
        private readonly CaptureFrameSource _source = new(clock);
        private bool _aligned;
        public TimeProvider Clock => clock;
        public Guid BattleId => battleId;
        public List<CombatBattleHostInput> Requests { get; } = [];

        public CombatBattleObservation ObserveTarget()
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            var source = _source.Next();
            EnemySeekDecision target;
            if (game.SkillInputs == 0)
                target = new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                    new(900, 400, 30, 30, 900), 1, SeekCueKind.DamageNumber);
            else if (!_aligned)
                target = new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.Left,
                    new(400, 480, 32, 28, 500, -90), 1, SeekCueKind.DirectionIndicator);
            else
                target = new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                    // Eight-pixel normal health bars are already near for the unchanged melee exit threshold of six.
                    new(930, 400, 60, 8, 480), 1, SeekCueKind.HealthBar);
            return new(source, battleId, CombatObservationQuality.Available, target, 1920, 1080, 1)
            { Motion = MotionStatus.Normal, Control = new(MotionStatus.Normal, false) };
        }

        public bool PrepareInputEvidence(CombatBattleHostInput input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(input);
            return true;
        }
        public ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(input.Source.IsFresh(clock, TimeSpan.FromMilliseconds(150)));
            if (input.Kind == CombatBattleHostInputKind.Camera) { events.Add("camera"); _aligned = true; }
            else if (input.Kind == CombatBattleHostInputKind.CameraReset) events.Add("camera-reset");
            else if (input.Kind == CombatBattleHostInputKind.Approach) { events.Add("approach"); _aligned = true; }
            else throw new InvalidOperationException($"Unexpected host input {input.Kind}");
            return ValueTask.FromResult(new CombatBattleHostInputResult(CombatBattleHostInputStatus.Sent, clock.GetTimestamp())
            { NativeRequested = 1, NativeSubmitted = 1 });
        }
        public PartySetupFinishObservation ObservePartyBar() =>
            throw new InvalidOperationException("This replay does not open the party UI or claim battle completion.");
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return ValueTask.CompletedTask;
        }
        public void ReleaseInput() { }
    }
}
