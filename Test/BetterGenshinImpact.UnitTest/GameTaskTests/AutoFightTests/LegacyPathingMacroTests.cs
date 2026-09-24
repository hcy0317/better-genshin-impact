using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using Vanara.PInvoke;
using System.Text.Json;
using BetterGenshinImpact.GameTask;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class LegacyPathingMacroTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task TransientUnknownAtBoundaryWaitsWithoutReplayingMacro(int unknownAt)
    {
        var io = new MacroReplay { UnknownAt = unknownAt, UnknownCount = 2 };
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keypress(e)", false), ["钟离"]), (_, _) => throw new Exception(), default);
        Assert.Equal(CombatExecutionKind.Completed, result.Kind);
        Assert.Equal(new[] { "KeyDown:VK_E", "KeyUp:VK_E" }, io.Inputs);
        Assert.True(io.Observations >= 4);
        Assert.InRange((io.Time.GetUtcNow() - io.Start).TotalSeconds, .29, 1);
    }

    [Fact]
    public async Task TrailingXCleanupFailureBlocksOwnerWithoutRepeatingUnknownRelease()
    {
        var original = new IOException("original X-up failure");
        var io = new MacroReplay { FaultAt = 2, Fault = CombatBattleHostInputStatus.Unknown, FaultError = original };
        var session = new PathingMacroSession(io);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(x)", false), ["钟离"]), (_, _) => throw new Exception(), default));
        Assert.Same(original, failure.InnerException);
        Assert.Equal(new[] { "KeyDown:VK_X", "KeyUp:VK_X" }, io.Inputs);
        Assert.Null(io.Coordinator.TryAcquire(Guid.NewGuid(), () => { }));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => session.Release()));
        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => session.Dispose()));
    }

    [Fact]
    public async Task Recorded07TrailingXUsesLegacyFragmentCleanupInsteadOfAbortingRoute()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(x)", false), ["钟离"]), (_, _) => throw new Exception(), default);
        Assert.Equal(CombatExecutionKind.Completed, result.Kind);
        Assert.Equal(new[] { "KeyDown:VK_X", "KeyUp:VK_X" }, io.Inputs);
        Assert.False(session.HasTail);
        using var next = io.Coordinator.TryAcquire(Guid.NewGuid(), () => { });
        Assert.NotNull(next);
    }

    [Fact]
    public void NativeCleanupStillSubmitsOnlyUpAfterRealTaskScopeFailure()
    {
        using var scope = TaskExecutionScope.BeginOwned();
        Assert.NotNull(Record.Exception(() => TaskExecutionScope.StopUnconfirmedCombat("terminal test")));
        var submissions = 0;
        var dispatcher = new WindowsInputMessageDispatcher(null,
            inputs => { submissions++; return (uint)inputs.Length; }, () => 0);
        var io = new NativePathingMacroIo(() => "offline", input =>
        {
            Assert.Equal(PathingMacroInputKind.KeyUp, input.Kind);
            dispatcher.DispatchInput(new User32.INPUT[1]);
        });
        var receipt = io.Release(new(PathingMacroInputKind.KeyUp, User32.VK.VK_W));
        Assert.Equal(CombatBattleHostInputStatus.Sent, receipt.Status);
        Assert.Equal(1, receipt.NativeSubmitted);
        Assert.Equal(1, submissions);
        Assert.Throws<ArgumentException>(() => io.Release(new(PathingMacroInputKind.KeyDown, User32.VK.VK_W)));
    }

    [Fact]
    public async Task FailedFirstCleanupStillAttemptsOtherHeldKeysButNeverTransfersOwnership()
    {
        using var cancellation = new CancellationTokenSource();
        var io = new MacroReplay { FaultAt = 3, Fault = CombatBattleHostInputStatus.Unknown,
            AfterDelay = () => cancellation.Cancel() };
        var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(q),keydown(w),wait(.1)", false), ["琴"]),
            (_, _) => throw new Exception(), cancellation.Token));
        Assert.Equal(new[] { "KeyDown:VK_Q", "KeyDown:VK_W", "KeyUp:VK_Q", "KeyUp:VK_W" }, io.Inputs);
        Assert.Null(io.Coordinator.TryAcquire(Guid.NewGuid(), () => { }));
        Assert.Throws<InvalidOperationException>(() => session.Dispose());
    }

    [Fact]
    public async Task CancellationDuringOriginalWaitReleasesHeldKeyAndStopsLaterInput()
    {
        using var cancellation = new CancellationTokenSource();
        var io = new MacroReplay { AfterDelay = () => cancellation.Cancel() };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w),wait(0.5),keypress(e)", false), ["琴"]),
            (_, _) => throw new Exception(), cancellation.Token));
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
        Assert.False(session.HasTail);
    }

    [Fact]
    public async Task NativeSegmentCannotRenewWholeFragmentBudget()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext("wait(.1);琴 q;keypress(t)", false), ["琴"]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ExecuteAsync(plan, (_, _) =>
        {
            io.Time.Advance(TimeSpan.FromSeconds(30));
            return Task.FromResult(new CombatExecutionResult(CombatExecutionKind.Completed, "late-native"));
        }, default));
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UnknownEntryOrPostSceneCannotProceedToNextSegment(int unknownAt)
    {
        var io = new MacroReplay { UnknownAt = unknownAt };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w),wait(.1),keyup(w);琴 e", false), ["琴"]),
            (_, _) => throw new Exception("未知不能进入具名段"), default));
        Assert.False(session.HasTail);
    }

    [Fact]
    public async Task All38RecordedNatlanMacrosKeepOriginalCommandsAndCompletePhysicalSegments()
    {
        using var corpus = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,
            "Fixtures", "Combat", "recorded-natlan-macros.json")));
        Assert.Equal(38, corpus.RootElement.GetArrayLength());
        foreach (var item in corpus.RootElement.EnumerateArray())
        {
            var script = CombatScriptParser.ParseContext(item.GetProperty("script").GetString()!, false);
            var plan = LegacyPathingMacroPlan.Create(script, ["枫原万叶", "琴", "芙宁娜"]);
            var planned = plan.Segments.SelectMany(segment => segment.Commands).ToArray();
            // 用户新规则只抑制相邻的琴聚物替代块，匿名/万叶及其他角色顺序保持。
            Assert.Equal(script.CombatCommands.Where(command => command.Name != "琴"), planned.Where(command => command.Name != "琴"));
            if (script.CombatCommands.Any(command => command.Name == "枫原万叶")) Assert.DoesNotContain(planned, command => command.Name == "琴");
            var io = new MacroReplay();
            using var session = new PathingMacroSession(io);
            var result = await session.ExecuteAsync(plan, (commands, _) =>
            {
                // NativeRunner入口的完整命令必须保留配对E；这些指令不送入raw物理端口。
                var down = commands.Count(command => command.Method == Method.KeyDown);
                var up = commands.Count(command => command.Method == Method.KeyUp);
                Assert.Equal(down, up);
                return Task.FromResult(new CombatExecutionResult(CombatExecutionKind.Completed, "native-boundary"));
            }, default);
            Assert.Equal(CombatExecutionKind.Completed, result.Kind);
        }
    }

    [Theory]
    [InlineData((int)CombatBattleHostInputStatus.Failed)]
    [InlineData((int)CombatBattleHostInputStatus.Unknown)]
    [InlineData((int)CombatBattleHostInputStatus.NotSent)]
    public async Task NativeFailureStopsWithoutReplayingAndCleansOriginalKey(int fault)
    {
        var io = new MacroReplay { FaultAt = 1, Fault = (CombatBattleHostInputStatus)fault };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w),keypress(e),keypress(t)", false), ["琴"]),
            (_, _) => throw new Exception(), default));
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
        using var next = io.Coordinator.TryAcquire(Guid.NewGuid(), () => { });
        Assert.NotNull(next);
    }

    [Fact]
    public async Task MappingChangeStillReleasesOriginalPhysicalKey()
    {
        var io = new MacroReplay { Mapping = _ => User32.VK.VK_Z };
        using var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        io.Mapping = _ => User32.VK.VK_Y;
        session.AdoptNavigation(io.Observe(), true, default);
        Assert.False(session.HasTail);
        Assert.Equal(new[] { "KeyDown:VK_Z", "KeyUp:VK_Z" }, io.Inputs);
    }

    [Fact]
    public async Task FailedReleaseRetainsCoordinatorOwnership()
    {
        var io = new MacroReplay { FaultAt = 2, Fault = CombatBattleHostInputStatus.Unknown };
        var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        Assert.Throws<InvalidOperationException>(() => session.Release());
        Assert.Null(io.Coordinator.TryAcquire(Guid.NewGuid(), () => { }));
        Assert.Throws<InvalidOperationException>(() => session.Dispose());
        Assert.Equal(2, io.Inputs.Count);
    }

    [Fact]
    public async Task TailDeadlineBeforeNavigationReleasesAndCannotBeRenewed()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WaitAsync(9000, default));
        Assert.False(session.HasTail);
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
    }

    [Fact]
    public async Task Recorded638TailIsNotRepressedAndNavigationUpRetiresOwner()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
            CombatScriptParser.ParseContext("keydown(w)", false), ["琴"]), (_, _) => throw new Exception(), default);
        await session.WaitAsync(1000, default);
        session.AdoptNavigation(io.Observe(), validPosition: true, default);
        Assert.True(session.TryNavigationForward(down: true, default));
        Assert.True(session.TryNavigationForward(down: false, default));
        Assert.Equal(new[] { "KeyDown:VK_W", "KeyUp:VK_W" }, io.Inputs);
        using var nextOwner = io.Coordinator.TryAcquire(Guid.NewGuid(), () => { });
        Assert.NotNull(nextOwner);
    }

    [Fact]
    public async Task Recorded619ExitMacroKeepsTimingWithoutPerKeyObservation()
    {
        var io = new MacroReplay();
        using var session = new PathingMacroSession(io);
        var script = CombatScriptParser.ParseContext(
            "wait(0.5),keydown(Q),keypress(f),wait(0.8),keypress(f),keyup(Q),wait(0.1),j,keypress(f)", false);
        var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(script, ["琴"]),
            (_, _) => throw new Exception("物理宏不能选择人形角色"), default);
        Assert.True(result.CanContinue);
        Assert.Equal(new[] { "KeyDown:VK_Q", "KeyDown:VK_F", "KeyUp:VK_F", "KeyDown:VK_F", "KeyUp:VK_F", "KeyUp:VK_Q", "KeyDown:VK_SPACE", "KeyUp:VK_SPACE", "KeyDown:VK_F", "KeyUp:VK_F" }, io.Inputs);
        Assert.Equal(2, io.Observations);
        // 原wait共1.4秒，四个35ms脉冲，最后60ms响应等待；不逐键取帧。
        Assert.InRange(io.Time.GetUtcNow() - io.Start, TimeSpan.FromSeconds(1.6), TimeSpan.FromSeconds(1.61));
    }

    [Fact]
    public async Task CannonProgramKeepsFireAndExitWithinItsOwnedPhysicalSequence()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.Cannon };
        using var session = new PathingMacroSession(io);
        var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(
            "keypress(f),wait(.1),keypress(w),keypress(RETURN),wait(.1),keypress(ESCAPE)", false), ["琴"]);
        var result = await session.ExecuteAsync(plan,
            (_, _) => throw new InvalidOperationException("Cannon program must not enter the humanoid skill runner"), default);
        Assert.True(result.CanContinue);
        Assert.Contains("KeyDown:VK_RETURN", io.Inputs);
        Assert.Contains("KeyDown:VK_ESCAPE", io.Inputs);
        Assert.False(session.HasTail);
    }

    [Theory]
    [InlineData("keypress(e)")]
    [InlineData("keypress(q)")]
    [InlineData("keydown(w)")]
    [InlineData("moveby(10,0)")]
    public async Task CannonDoesNotGrantSkillMouseOrNavigationTail(string text)
    {
        var io = new MacroReplay { Scene = PathingMacroScene.Cannon };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(
            LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext(text, false), ["琴"]),
            (_, _) => throw new InvalidOperationException("not admitted"), default));
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task CannonWithoutFirePromptCannotSendReturn()
    {
        var io = new MacroReplay { Scene = PathingMacroScene.Cannon, FirePrompt = false };
        using var session = new PathingMacroSession(io);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ExecuteAsync(
            LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext("keypress(f),keypress(RETURN),keypress(ESCAPE)", false), ["琴"]),
            (_, _) => throw new Exception(), default));
        Assert.DoesNotContain("KeyDown:VK_RETURN", io.Inputs);
    }

    [Fact]
    public async Task ThreeRecordedCannonProgramsCrossWorldBoundariesWithoutReplayingInput()
    {
        using var programs = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Combat", "s56-cannon-programs.json")));
        Assert.Equal(3, programs.RootElement.GetArrayLength());
        foreach (var item in programs.RootElement.EnumerateArray())
        {
            var text = item.GetProperty("script").GetString()!;
            var io = new MacroReplay { Scene = PathingMacroScene.World, CrossCannonScenes = true };
            using var session = new PathingMacroSession(io);
            var result = await session.ExecuteAsync(LegacyPathingMacroPlan.Create(
                CombatScriptParser.ParseContext(text, false), ["琴"]), (_, _) => throw new Exception("unexpected humanoid runner"), default);
            Assert.True(result.CanContinue);
            Assert.Equal(PathingMacroScene.World, io.Scene);
            Assert.False(session.HasTail);
            Assert.Equal(text.Split("keypress(RETURN)").Length - 1, io.Inputs.Count(x => x == "KeyDown:VK_RETURN"));
            Assert.Equal(text.Split("keypress(ESCAPE)").Length - 1, io.Inputs.Count(x => x == "KeyDown:VK_ESCAPE"));
        }
    }

    internal sealed class MacroReplay : IPathingMacroIo
    {
        internal FakeTimeProvider Time { get; } = new();
        public TimeProvider Clock => Time;
        public CombatInputCoordinator Coordinator { get; } = new();
        internal DateTimeOffset Start;
        internal List<string> Inputs { get; } = [];
        internal int Observations;
        internal Func<User32.VK, User32.VK> Mapping = key => key;
        internal int FaultAt;
        internal CombatBattleHostInputStatus Fault;
        internal Exception? FaultError;
        internal Action? AfterDelay;
        internal int UnknownAt;
        internal int UnknownCount = int.MaxValue;
        internal PathingMacroScene Scene = PathingMacroScene.Transformed;
        internal bool FirePrompt = true, CrossCannonScenes;
        private readonly CaptureFrameSource _source;
        internal CaptureFrameStamp NextFrame() { Time.Advance(TimeSpan.FromMilliseconds(1)); return _source.Next(); }
        public MacroReplay() { _source = new(Time); Start = Time.GetUtcNow(); }
        public PathingMacroObservation Observe(string phase = "boundary")
        {
            Observations++;
            Time.Advance(TimeSpan.FromMilliseconds(1));
            var unknown = UnknownAt > 0 && Observations >= UnknownAt && Observations - UnknownAt < UnknownCount;
            return new(unknown ? PathingMacroScene.Unknown : Scene, _source.Next(), Scene == PathingMacroScene.Cannon && FirePrompt);
        }
        public User32.VK Map(User32.VK key) => Mapping(key);
        public CombatBattleHostInputResult Send(PathingMacroInput input, Action admit)
        {
            admit();
            Inputs.Add($"{input.Kind}:{input.Key}");
            if (CrossCannonScenes && input.Kind == PathingMacroInputKind.KeyUp)
            {
                if (input.Key == User32.VK.VK_ESCAPE) Scene = PathingMacroScene.World;
                if (input.Key == User32.VK.VK_F) Scene = PathingMacroScene.Cannon;
            }
            if (Inputs.Count == FaultAt) return new(Fault, Reason: "simulated-native-failure", Error: FaultError);
            return new(CombatBattleHostInputStatus.Sent, Clock.GetTimestamp())
            { NativeRequested = 1, NativeSubmitted = 1, ObservableAfterTimestamp = Clock.GetTimestamp() };
        }
        public Task Delay(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Time.Advance(TimeSpan.FromMilliseconds(milliseconds));
            AfterDelay?.Invoke();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public void Recorded620MixedHeldMovementRemainsOneNativeSequence()
    {
        var script = CombatScriptParser.ParseContext(
            "keydown(w),wait(0.1),dash,wait(0.3),attack(0.22),j,wait(0.35),keyup(w),wait(0.25),j", false);
        var plan = LegacyPathingMacroPlan.Create(script, ["琴"]);
        var held = plan.Segments[0];
        Assert.False(held.IsRaw);
        Assert.Equal(8, held.Commands.Count);
        Assert.Equal(Method.KeyDown, held.Commands[0].Method);
        Assert.Equal(Method.KeyUp, held.Commands[^1].Method);
        Assert.True(plan.Segments[1].IsRaw);
        Assert.Equal(2, plan.Segments[1].Commands.Count);
    }
}
