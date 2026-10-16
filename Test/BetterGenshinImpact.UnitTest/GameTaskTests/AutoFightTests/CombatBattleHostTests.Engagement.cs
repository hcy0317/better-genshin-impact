using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatBattleHostTests
{
    [RecordedMatrixSceneTests.RecordedMatrixFact]
    public async Task RecordedMatrixEffectCannotDriveMeleeCameraOrApproachAndHudLossCannotComplete()
    {
        var scene = BetterGenshinImpact.GameTask.AutoBoss.AutoBossTask.SceneForBoss("半永恒统辖矩阵");
        foreach (var sequence in new[] { 2755, 3000, 3178, 3180 })
        {
            using var pixels = Cv2.ImRead(System.IO.Path.Combine(
                Environment.GetEnvironmentVariable("BGI_MATRIX_SCENE_EVIDENCE_ROOT")!, $"evidence-{sequence}.png"));
            Assert.False(pixels.Empty());
            var clock = new FakeTimeProvider();
            var game = new ReturningGame(clock);
            using var flow = CreateFlow(false, game, clock);
            var io = new ReplayIo(clock, flow.Context.BattleId);
            io.TargetFactory = stamp =>
            {
                // Repeated static pixels model a persistent effect, not new gameplay evidence.
                using var frame = new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = stamp };
                var decision = AvatarRecognition.ReadPassiveTargetEvidence(frame, scene).Decision;
                return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, decision, 1920, 1080)
                { Scene = scene, FixedTopHealth = decision.FixedTopHealth,
                    Motion = MotionStatus.Normal, Control = new(MotionStatus.Normal, false) };
            };
            using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = true });
            var sawMelee = false;
            for (var i = 0; i < 40; i++)
            {
                sawMelee |= flow.EngagementDemand == CombatEngagementDemand.Melee;
                Assert.NotEqual(CombatBattleHostResult.Completed, await host.AdvanceAsync(flow, default));
            }
            Assert.True(sawMelee);
            Assert.DoesNotContain(io.Inputs, input => input.Kind is CombatBattleHostInputKind.Camera or
                CombatBattleHostInputKind.Approach or CombatBattleHostInputKind.TargetLock);
            Assert.True(game.Inputs > 0);
        }
    }

    [Fact]
    public async Task ProductionMatrixSceneDoesNotTurnItsHudIntoForwardOrLockInput()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var scene = BetterGenshinImpact.GameTask.AutoBoss.AutoBossTask.SceneForBoss("半永恒统辖矩阵");
        var decision = AutoFightSeek.SelectSeekDecision([new(721, 82, 6, 10, 60)], 1920, 1080, scene: scene);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            decision, 1920, 1080)
            { Scene = scene, FixedTopHealth = decision.FixedTopHealth,
                Motion = MotionStatus.Normal, Control = new(MotionStatus.Normal, false) };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false });
        for (var i = 0; i < 40; i++) await host.AdvanceAsync(flow, default);
        Assert.DoesNotContain(io.Inputs, input => input.Kind is
            CombatBattleHostInputKind.Approach or CombatBattleHostInputKind.TargetLock);
        Assert.True(game.Inputs > 0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FixedSceneTargetLockRequiresExplicitPermissionAndNeverPressesForward(bool allow, bool atomic)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = atomic ? NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("""
            strategy(loop=battle)
            segment(output,atomic) { 琴 attack(0.1,required),attack(0.1,required) }
            """), game, clock) : CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var scene = new CombatSceneCapabilities(new(.34, .08, .66, .12), true, allow);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 100, 6, 8, 48), 1,
                SeekCueKind.FixedTopHealth), 1920, 1080)
            { Scene = scene, Motion = MotionStatus.Normal, Control = new(MotionStatus.Normal, false) };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false });
        for (var i = 0; i < 40; i++) await host.AdvanceAsync(flow, default);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
        Assert.Equal(allow && !atomic ? 1 : 0, io.Inputs.Count(input => input.Kind == CombatBattleHostInputKind.TargetLock));
        Assert.True(game.Inputs > 0);
    }

    [Fact]
    public async Task OneThickerFrameCannotEndMeleeApproachBeforeStableConfirmation()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var height = 4;
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(height < 6 ? AutoFightSeekAction.ApproachVisibleEnemy : AutoFightSeekAction.KeepFighting,
                EnemyIndicatorDirection.None, new(920, 400, 80, height, 80 * height), 1, SeekCueKind.HealthBar), 1920, 1080)
            { Control = new(MotionStatus.Normal, false), Motion = MotionStatus.Normal };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false });
        for (var i = 0; i < 30 && io.Inputs.Count == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.Single(io.Inputs);
        height = 6;
        clock.Advance(TimeSpan.FromMilliseconds(70));
        await host.AdvanceAsync(flow, default);
        Assert.Equal(0, game.Inputs);
        height = 5;
        for (var i = 0; i < 4; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(0, game.Inputs);
        height = 6;
        for (var i = 0; i < 30 && game.Inputs == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.True(game.Inputs > 0);
    }

    [Fact]
    public void HighOrdinaryBarWithoutASceneDeclarationIsNotInventedAsFixedHud()
    {
        var decision = AutoFightSeek.SelectSeekDecision([new(920, 100, 80, 5, 400)], 1920, 1080);
        Assert.NotEqual(SeekCueKind.FixedTopHealth, decision.Cue);
    }

    [Fact]
    public async Task DueMaintenanceAndAtomicBodiesDoNotGrantEngagementPermission()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("""
            strategy(loop=battle)
            timing(盾窗,duration=10)
            钟离 e(record=护盾,timing=盾窗,watch=护盾,before=4)
            琴 attack(0.1,required)
            """), game, clock);
        Assert.Equal(CombatEngagementDemand.Unknown, flow.EngagementDemand);
        await flow.StepAsync(default);
        Assert.Equal(CombatEngagementDemand.Melee, flow.EngagementDemand);
        clock.Advance(TimeSpan.FromSeconds(7));
        Assert.Equal(CombatEngagementDemand.Unknown, flow.EngagementDemand);
        var before = game.Inputs;
        await flow.StepAsync(default);
        Assert.True(game.Inputs > before);
        using var atomic = NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("""
            strategy(loop=battle)
            segment(output,atomic) { 琴 attack(0.1,required),attack(0.1,required) }
            """), new ReturningGame(clock), clock);
        Assert.Equal(CombatEngagementDemand.Unknown, atomic.EngagementDemand);
        await atomic.StepAsync(default);
        Assert.True(atomic.IsAtomic);
        Assert.Equal(CombatEngagementDemand.Unknown, atomic.EngagementDemand);
    }

    [Fact]
    public async Task FarMeleeTargetApproachesAtTheFirstSafeBoundaryBeforeAttacking()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(920, 400, 80, 4, 320), 1, SeekCueKind.HealthBar), 1920, 1080)
            { Control = new(MotionStatus.Normal, false), Motion = MotionStatus.Normal };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false });
        for (var i = 0; i < 30 && io.Inputs.Count == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostInputKind.Approach, Assert.Single(io.Inputs).Kind);
        Assert.Equal(0, game.Inputs);
        Assert.True(flow.Context.Now < 2);
    }

    [Fact]
    public async Task RecentDamageShortLossWaitsWithoutPartyAndDoesNotRenewItsWindow()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                new(920, 400, 80, 20, 1600), 1, SeekCueKind.DamageNumber), 1920, 1080, 1);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        await host.AdvanceAsync(flow, default);
        io.TargetFactory = null;
        for (var i = 0; i < 5; i++) await host.AdvanceAsync(flow, default);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.OpenParty);
        Assert.Equal("recent-progress-awaiting-target", host.Reason);
        for (var i = 0; i < 80 && !io.Inputs.Any(input => input.Kind == CombatBattleHostInputKind.OpenParty); i++)
            await host.AdvanceAsync(flow, default);
        Assert.Contains(io.Inputs, input => input.Kind == CombatBattleHostInputKind.OpenParty);
        Assert.True(flow.Context.Now < 3);
    }
}
