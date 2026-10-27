using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Fact]
    public async Task PublicTowerSessionExecutesNativeRoundsThenAdvancesOnlyOnConfirmedExternalProgress()
    {
        using var task = BetterGenshinImpact.GameTask.TaskExecutionScope.BeginOwned();
        using var lifetime = new ScriptAsyncLifetime(default);
        var clock = new FakeTimeProvider();
        var source = new Fischless.GameCapture.CaptureFrameSource(clock);
        var backend = new SessionReplayBackend();
        var owners = new BetterGenshinImpact.Core.Input.InputSessionOwnership(backend);
        var ready = new TaskCompletionSource<Action>(TaskCreationOptions.RunContinuationsAsynchronously);
        var attacks = 0;
        var progressSubmitted = false;
        var perceptionRetired = false;
        var dispatcher = new BetterGenshinImpact.Core.Script.Dependence.Dispatcher(new object(), null!,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<BetterGenshinImpact.Core.Script.Dependence.Dispatcher>.Instance,
            new(clock, owners.Acquire, async (script, _, bind, token) =>
            {
                var complete = await ready.Task.WaitAsync(token);
                using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 attack(.1)"))
                { Actors = [new("钟离", 1)], ExternalClockAdvance = true };
                physical.SetFrontActor("钟离");
                using var flow = NativeCombatFlowRunner.Create(CombatScriptExecutor.ExternalSessionCommands(script), physical, loop: true);
                var perceptionStarted = false;
                async Task Perceive(Guid battle, CancellationToken ct)
                {
                    Assert.Equal(flow.Context.BattleId, battle);
                    perceptionStarted = true;
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    finally { perceptionRetired = true; }
                }
                var scene = new SceneReplayIo(clock, flow.Context.BattleId) { BeforeObservation = () =>
                {
                    Assert.True(perceptionStarted);
                    attacks = physical.Primitives.Count(command => command.Method == Method.Attack);
                    if (attacks >= 2 && !progressSubmitted) { progressSubmitted = true; complete(); }
                } };
                using var host = new CombatBattleHost(scene, new() { ExternalCompletionAuthority = true,
                    FinishDetectionEnabled = false, SeekEnabled = false });
                await CombatScriptExecutor.RunExternalSessionCoreAsync(flow, host, bind, Perceive, token);
            }));
        var session = dispatcher.CreateCombatSession("钟离 attack(.1)", "tower-1", source.Next(), 0, 2);
        ready.SetResult(() =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            Assert.True(session.SubmitProgress(session.SessionId, session.TargetId, source.Next(), 1, 2, false));
            Assert.False(session.IsCompleted);
            clock.Advance(TimeSpan.FromMilliseconds(210));
            Assert.True(session.SubmitProgress(session.SessionId, session.TargetId, source.Next(), 1, 2, false));
        });
        try
        {
            var result = await session.WaitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("CompletedByExternalProgress", result.Status);
            Assert.Equal("target-progress-increased", result.Reason);
            Assert.Equal(2, attacks);
            Assert.True(perceptionRetired);
            Assert.Equal(1, backend.Releases);
            using var nextOwner = owners.Acquire();
        }
        finally { session.Cancel(); await session.DisposeAsync(); await lifetime.CloseAsync(); }
    }

    private sealed class SessionReplayBackend : BetterGenshinImpact.Core.Input.IInputBackend
    {
        internal int Releases;
        public BetterGenshinImpact.Core.Input.InputBackendKind Kind => BetterGenshinImpact.Core.Input.InputBackendKind.Win32;
        public BetterGenshinImpact.Core.Input.IInputChannel Foreground { get; } = new SessionReplayChannel();
        public BetterGenshinImpact.Core.Input.IInputChannel Background => Foreground;
        public void ReleaseAll() => Releases++;
        public void Dispose() { }
    }

    private sealed class SessionReplayChannel : BetterGenshinImpact.Core.Input.InputChannelBase
    {
        protected override void OnKeyDown(Vanara.PInvoke.User32.VK key) => throw new InvalidOperationException("Unexpected real input path");
        protected override void OnKeyUp(Vanara.PInvoke.User32.VK key) { }
        protected override void OnMouseButton(BetterGenshinImpact.Core.Input.InputMouseButton button, bool down)
        { if (down) throw new InvalidOperationException("Unexpected real input path"); }
        protected override void OnMoveBy(int x, int y) => throw new InvalidOperationException("Unexpected real input path");
        protected override void OnMoveTo(double x, double y) => throw new InvalidOperationException("Unexpected real input path");
        protected override void OnScroll(int clicks) => throw new InvalidOperationException("Unexpected real input path");
    }

    [Fact]
    public async Task MiningEntryBindsItsBuiltInActionToTheRouteAndBattleWithoutClaimingOreDestruction()
    {
        var clock = new FakeTimeProvider();
        var logger = new SourceIdentityLogger();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(hold,wait)")) { Logger = logger };
        io.SetFrontActor("钟离");
        var point = new AutoPathingTests.PathReplay().Point("walk");
        point.Id = 15;
        point.PathingTaskFileName = "07-跳崖点东x18.json";
        await new BetterGenshinImpact.GameTask.AutoPathing.Handler.MiningHandler(io).RunAsync(default, point);
        var boundaries = logger.Messages.Where(line => line.Contains("MINING_ACTION_BOUNDARY")).ToArray();
        Assert.Equal(2, boundaries.Length);
        Assert.All(boundaries, line => { Assert.Contains("07-跳崖点东x18.json", line); Assert.Contains("node=15", line); });
        Assert.Contains("钟离 e(hold,wait)", boundaries[0]);
        Assert.Contains("oreState=not-observed", boundaries[1]);
        Assert.Single(io.Inputs);
        Assert.Empty(io.Primitives);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BodyBlockedApproachHandsTheOriginalNodeToNativeMiningOnlyAfterArrival(bool cancelAtArrival)
    {
        using var cancellation = new CancellationTokenSource();
        var path = new AutoPathingTests.PathReplay(cancellation.Token) { EmitReceipts = true };
        var position = new OpenCvSharp.Point2f(104, 100);
        var held = BetterGenshinImpact.Core.Simulator.Extensions.GIActions.MoveForward;
        var moving = false;
        var clear = false;
        path.PositionAt = _ => position;
        path.OnInput = (action, type) =>
        {
            if (type == BetterGenshinImpact.Core.Simulator.Extensions.KeyType.KeyDown) { held = action; moving = true; }
            if (type == BetterGenshinImpact.Core.Simulator.Extensions.KeyType.KeyUp && action == held) moving = false;
        };
        path.AfterDelay = ms =>
        {
            if (!moving) return;
            if (held == BetterGenshinImpact.Core.Simulator.Extensions.GIActions.MoveLeft) position.Y += ms * .02f;
            clear |= position.Y >= 102;
            if (held == BetterGenshinImpact.Core.Simulator.Extensions.GIActions.MoveForward && clear)
            { position.X = Math.Max(100, position.X - .6f); position.Y = Math.Max(100, position.Y - .3f); }
        };
        var point = path.Point("walk");
        point.Action = "mining";
        point.Id = 15;
        var clock = path.Clock;
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(hold,wait)")) { ExternalClockAdvance = true };
        io.SetFrontActor("钟离");
        await path.Executor.MoveCloseTo(point);
        Assert.True(clear);
        Assert.True(Math.Pow(position.X - 100, 2) + Math.Pow(position.Y - 100, 2) < 4);
        Assert.Empty(io.Inputs);
        if (cancelAtArrival) cancellation.Cancel();
        // These are the same arrival and action boundaries used by PathExecutor's route loop.
        Task Dispatch() => BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.DispatchArrivalActionAsync(
            new BetterGenshinImpact.GameTask.AutoPathing.Handler.MiningHandler(io), point, null, cancellation.Token);
        if (cancelAtArrival)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Dispatch);
            Assert.Empty(io.Inputs);
        }
        else
        {
            await Dispatch();
            Assert.Equal(Method.Skill, Assert.Single(io.Inputs).Skill);
            Assert.True(io.FirstShieldConfirmed >= 0 && double.IsFinite(io.FirstShieldConfirmed));
        }
        Assert.DoesNotContain(path.RecoveryInputs, input => input.Action is
            BetterGenshinImpact.Core.Simulator.Extensions.GIActions.Jump or BetterGenshinImpact.Core.Simulator.Extensions.GIActions.MoveBackward);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecordedAfternoonWatchtowerTextKeepsRequiredBurstSemantics(bool energyReady)
    {
        const string text = "钟离 e(hold),q(optional),attack(1.2)\n班尼特 e,q,attack(1.2)\n娜维娅 e,q,attack(1.2)\n香菱 e,q,attack(1.2)";
        Assert.Equal("EDF4BDE8844B17FEB9D0D7DDC8F0D1C31991BEB4983D021708055C1EEF203111",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))));
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, energyReady, 50, LoadProgram(text))
        { Actors = [new("钟离", 1), new("娜维娅", 2), new("班尼特", 3), new("香菱", 4)], CompleteSkillReceipts = true };
        foreach (var actor in io.Actors) io.ScheduleEnergy(0, actor.Name, energyReady);
        io.SetFrontActor("钟离");
        using var flow = NativeCombatFlowRunner.Create(
            CombatScriptExecutor.ExternalSessionCommands(CombatScriptParser.ParseContext(text)), io, loop: false);
        var result = await RunBoundedReplayRound(flow, io);
        Assert.True(io.FirstShieldConfirmed >= 0 && double.IsFinite(io.FirstShieldConfirmed));
        Assert.Contains(io.Primitives, command => command.Name == "钟离" && command.Method == Method.Attack);
        if (energyReady)
        {
            Assert.Equal(CombatFlowResult.Succeeded, result);
            Assert.Equal(4, io.Primitives.Count(command => command.Method == Method.Attack));
        }
        else
        {
            Assert.Equal(CombatFlowResult.Deferred, result);
            Assert.Single(io.Primitives.Where(command => command.Method == Method.Attack));
            Assert.DoesNotContain(io.Inputs, input => input.Skill == Method.Burst);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalPerceptionFailureOrUnexpectedExitStopsBeforeAnyAttack(bool unexpectedExit)
    {
        var clock = new FakeTimeProvider();
        const string text = "钟离 attack(.1)";
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(text)) { Actors = [new("钟离", 1)] };
        io.SetFrontActor("钟离");
        using var flow = NativeCombatFlowRunner.Create(
            CombatScriptExecutor.ExternalSessionCommands(CombatScriptParser.ParseContext(text)), io, loop: true);
        using var host = new CombatBattleHost(new SceneReplayIo(clock, flow.Context.BattleId),
            new() { ExternalCompletionAuthority = true, FinishDetectionEnabled = false, SeekEnabled = false });
        var failure = new IOException("fixture-perception-failed");
        var actual = await Record.ExceptionAsync(() => CombatScriptExecutor.RunExternalSessionCoreAsync(
            flow, host, _ => { }, (_, _) => unexpectedExit ? Task.CompletedTask : Task.FromException(failure), default));
        if (unexpectedExit) Assert.Contains("感知循环意外退出", Assert.IsType<InvalidOperationException>(actual).Message);
        else Assert.Same(failure, actual);
        Assert.Empty(io.Primitives);
    }

    [Fact]
    public async Task ExternalSessionWaitsForPerceptionRetirementBeforeReturning()
    {
        var clock = new FakeTimeProvider();
        const string text = "钟离 attack(.1)";
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(text)) { Actors = [new("钟离", 1)] };
        io.SetFrontActor("钟离");
        using var flow = NativeCombatFlowRunner.Create(
            CombatScriptExecutor.ExternalSessionCommands(CombatScriptParser.ParseContext(text)), io, loop: true);
        using var cancellation = new CancellationTokenSource();
        var retiring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scene = new SceneReplayIo(clock, flow.Context.BattleId) { BeforeObservation = cancellation.Cancel };
        using var host = new CombatBattleHost(scene,
            new() { ExternalCompletionAuthority = true, FinishDetectionEnabled = false, SeekEnabled = false });
        async Task Perceive(Guid _, CancellationToken token)
        {
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { retiring.TrySetResult(); await release.Task; }
        }
        var task = CombatScriptExecutor.RunExternalSessionCoreAsync(flow, host, _ => { }, Perceive, cancellation.Token);
        try
        {
            await retiring.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(task.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Empty(io.Primitives);
    }

    [Fact]
    public async Task ExternalHostKeepsOneNativeBattleAcrossTwoRequiredShieldAndOptionalBurstRounds()
    {
        var clock = new FakeTimeProvider();
        const string text = "钟离 e(hold),q(optional),attack(1.2)";
        var parsed = CombatScriptParser.ParseContext(text);
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(text))
        { Actors = [new("钟离", 1)], CompleteSkillReceipts = true };
        io.SetFrontActor("钟离");
        using var flow = NativeCombatFlowRunner.Create(CombatScriptExecutor.ExternalSessionCommands(parsed), io, loop: true);
        var battle = flow.Context.BattleId;
        using var cancelled = new CancellationTokenSource();
        var observations = 0;
        var perceptionStarted = false;
        var perceptionStopped = false;
        async Task Perceive(Guid id, CancellationToken token)
        {
            Assert.Equal(battle, id);
            perceptionStarted = true;
            try { await Task.Delay(Timeout.Infinite, token); }
            finally { perceptionStopped = true; }
        }
        var scene = new SceneReplayIo(clock, battle)
        {
            BeforeObservation = () =>
            {
                Assert.True(perceptionStarted, "生产会话必须启动感知，不能仅靠测试注入的现成观察推进");
                Assert.Equal(battle, flow.Context.BattleId);
                if (io.Primitives.Count(command => command.Method == Method.Attack) >= 2) cancelled.Cancel();
                Assert.True(++observations < 2000, "两轮战斗未在有界观察内完成");
            }
        };
        using var host = new CombatBattleHost(scene, new() { ExternalCompletionAuthority = true,
            FinishDetectionEnabled = false, SeekEnabled = false, TimeoutSeconds = 30 });
        var bindings = new List<Guid>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CombatScriptExecutor.RunExternalSessionCoreAsync(flow, host, bindings.Add, Perceive, cancelled.Token));
        Assert.True(perceptionStopped);
        Assert.Equal(new[] { battle }, bindings);
        Assert.Equal(2, io.Primitives.Count(command => command.Method == Method.Attack));
        Assert.DoesNotContain(io.Inputs, input => input.Skill == Method.Burst);
        Assert.True(io.FirstShieldConfirmed >= 0);
    }

    [Fact]
    public async Task ExternalDefaultRequiredBurstCannotBeSilentlySkippedLikeALegacyFragment()
    {
        var clock = new FakeTimeProvider();
        const string text = "钟离 q,attack(.1)";
        var commands = CombatScriptExecutor.ExternalSessionCommands(CombatScriptParser.ParseContext(text));
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(text)) { Actors = [new("钟离", 1)] };
        io.SetFrontActor("钟离");
        using var flow = NativeCombatFlowRunner.Create(commands, io, loop: false);
        Assert.Equal(CombatFlowResult.Deferred, await RunBoundedReplayRound(flow, io));
        Assert.DoesNotContain(io.Primitives, command => command.Method == Method.Attack);
    }
}
