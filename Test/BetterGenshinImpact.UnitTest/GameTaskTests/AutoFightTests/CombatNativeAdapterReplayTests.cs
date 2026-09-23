using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Newtonsoft.Json.Linq;
using BetterGenshinImpact.GameTask.Common;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Vanara.PInvoke;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Time.Testing;
using Xunit.Abstractions;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

// B层：真实解析/调度/在途协议与选角策略，帧到达和游戏物理效果是可控外部边界。
public class CombatNativeAdapterReplayTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(MotionStatus.Fly, "wait(.35),j,wait(2),j", 2.35)]
    [InlineData(MotionStatus.Climb, "wait(.4),j,wait(1),j", 1.4)]
    public async Task AnonymousJumpAliasesUseOnlyTheExistingPathingPrimitive(MotionStatus motion, string script, double wait)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(.1)")) { ObservedMotion = motion };
        io.SetFrontActor("未识别形态");
        using var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseLineCommands(script, CombatScriptParser.CurrentAvatarName),
            io, false, purpose: CombatScriptExecutionPurpose.Pathing);
        Assert.Equal(CombatFlowResult.Succeeded, await RunBoundedReplayRound(runner, io));
        Assert.Equal(2, io.PathingPrimitives.Count(command => command.Method == Method.Jump));
        Assert.Empty(io.Selections);
        Assert.InRange(runner.Context.Now, wait, wait + 1);
    }

    [Theory]
    [InlineData("hud")]
    [InlineData("unknown-control")]
    [InlineData("breakout")]
    [InlineData("stale")]
    [InlineData("slow-control")]
    [InlineData("atomic")]
    [InlineData("combat")]
    [InlineData("named")]
    [InlineData("cancel")]
    [InlineData("deadline")]
    public async Task AnonymousJumpAdmissionRejectsInvalidEvidence(string fault)
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var io = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(.1)"))
        {
            InitialFrameFault = fault == "stale" ? "stale" : null,
            CombatHudVisible = fault != "hud",
            ControlReadCost = fault == "slow-control" ? 200 : fault == "deadline" ? 9000 : 0,
            BeforePathingInput = fault == "cancel" ? () => cancellation.Cancel() : null,
            ControlOverride = _ => fault == "unknown-control" ? default : new(MotionStatus.Fly, fault == "breakout")
        };
        io.SetFrontActor("未识别形态");
        var name = fault == "named" ? "琴" : CombatScriptParser.CurrentAvatarName;
        var commands = CombatScriptParser.ParseLineCommands("j", name);
        if (fault == "atomic") commands = [new("", "segment(start,atomic,required)"), .. commands, new("", "segment(end)")];
        using var runner = NativeCombatFlowRunner.Create(commands, io, false,
            purpose: fault == "combat" ? CombatScriptExecutionPurpose.Combat : CombatScriptExecutionPurpose.Pathing);
        var failure = await Record.ExceptionAsync(async () => await RunBoundedReplayRound(runner, io, ct: cancellation.Token));
        AssertNoHostBootstrapFailure(failure);
        Assert.DoesNotContain(io.PathingPrimitives, command => command.Method == Method.Jump);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldEAimingMoveByKeepsTheOriginalKeyUntilPairedUp(bool nonBlocking)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, nonBlocking ? 0 : 20, LoadProgram("琴 attack(.1)"))
            { NonBlockingHeldCapture = nonBlocking };
        io.SetFrontActor("琴");
        using var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseLineCommands(
            "keydown(e),wait(.2),moveby(0,-200),wait(.31),moveby(5,10),keyup(E)", "琴"), io, false);
        Assert.Equal(CombatFlowResult.Succeeded, await RunBoundedReplayRound(runner, io, nonBlocking ? 0 : 50));
        var down = Assert.Single(io.KeyEvents.Where(x => !x.Up));
        var up = Assert.Single(io.KeyEvents.Where(x => x.Up));
        Assert.True(up.At - down.At >= .51);
        Assert.Equal(new[] { "0,-200", "5,10" }, io.Primitives.Where(x => x.Method == Method.MoveBy).Select(x => string.Join(",", x.Args!)));
        Assert.All(io.MoveByHeldStates, held => Assert.True(held));
        Assert.Empty(io.Selections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldEAimingRejectsObservationsThatBecomeStaleDuringRecognition(bool activeRead)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(.1)"))
            { HeldActiveReadCost = activeRead ? 200 : 0, HeldControlReadCost = activeRead ? 0 : 200 };
        io.SetFrontActor("琴");
        using var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseLineCommands(
            "keydown(E),wait(.51),keyup(E)", "琴"), io, false);
        Assert.Equal(CombatFlowResult.Failed, await RunBoundedReplayRound(runner, io));
        Assert.Single(io.KeyEvents.Where(x => !x.Up));
        Assert.Contains(io.KeyEvents, x => x.Up);
        Assert.False(io.HoldingInput);
    }
    private static async Task<CombatFlowResult> RunBoundedReplayRound(NativeCombatFlowRunner runner, PhysicalReplay io,
        int pollingMilliseconds = 50, CancellationToken ct = default)
    {
        for (var i = 0; i < 600; i++)
        {
            var step = await runner.StepAsync(ct);
            if (step.RoundCompleted) return step.Result;
            // Host polling advances independently of a cheap/nonblocking capture.
            await io.DelayAsync(pollingMilliseconds, ct);
        }
        throw new TimeoutException("回放未在有界host步数内返回；不放宽生产期限");
    }

    private static void AssertNoHostBootstrapFailure(Exception? failure) =>
        Assert.DoesNotContain("Application host startup is prohibited", failure?.ToString() ?? "");

    [Theory]
    [InlineData("not-sent")]
    [InlineData("unknown")]
    [InlineData("partial")]
    [InlineData("repeat")]
    [InlineData("foreign")]
    [InlineData("wait-foreign")]
    public async Task AnonymousJumpReceiptAndFenceNeverAuthorizeAReplay(string fault)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(.1)"))
            { PathingJumpFault = fault };
        io.SetFrontActor("未识别形态");
        using var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseLineCommands(
            fault == "wait-foreign" ? "j,wait(.1),j" : "j,j", CombatScriptParser.CurrentAvatarName),
            io, false, purpose: CombatScriptExecutionPurpose.Pathing);
        var error = await Record.ExceptionAsync(async () => await RunBoundedReplayRound(runner, io));
        AssertNoHostBootstrapFailure(error);
        if (fault == "not-sent")
        {
            Assert.Null(error);
            Assert.Equal(2, io.PathingPrimitives.Count(command => command.Method == Method.Jump));
            Assert.Equal(3, io.PathingJumpAttempts);
            Assert.True(io.PathingJumpSources[1].IsAfter(io.PathingJumpSources[0]));
        }
        else Assert.Single(io.PathingPrimitives.Where(command => command.Method == Method.Jump));
        Assert.Empty(io.Selections);
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("partial")]
    [InlineData("mapping")]
    public async Task HeldEAimingMoveByFailureReleasesWithoutReplay(string fault)
    {
        var clock = new FakeTimeProvider();
        PhysicalReplay? replay = null;
        using var io = replay = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(.1)"))
        {
            MoveByReceiptFault = fault,
            HeldMapping = () => fault == "mapping" && replay!.KeyEvents.Count > 0 ? User32.VK.VK_R : User32.VK.VK_E
        };
        io.SetFrontActor("琴");
        using var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseLineCommands(
            "keydown(E),moveby(0,-200),wait(.51),keyup(E)", "琴"), io, false);
        var failure = await Record.ExceptionAsync(async () => await RunBoundedReplayRound(runner, io));
        AssertNoHostBootstrapFailure(failure);
        Assert.Single(io.KeyEvents.Where(x => !x.Up));
        Assert.Contains(io.KeyEvents, x => x.Up && x.Key == User32.VK.VK_E);
        Assert.Equal(fault == "mapping" ? 0 : 1, io.MoveByHeldStates.Count);
        Assert.Empty(io.Selections);
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData("班尼特", "q(required,record=恢复记录)")]
    [InlineData("钟离", "e(hold,required,record=恢复记录)")]
    public async Task CompleteIgnoredSkillCanRecoverOnceInsideTheOriginalConfirmationAttempt(string actor, string syntax)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram($"{actor} {syntax}");
        using var io = new PhysicalReplay(clock, false, 20, program)
            { Actors = [new(actor, 1)], DropFirstSkill = true, CompleteSkillReceipts = true };
        io.SetFrontActor(actor);
        io.ScheduleEnergy(0, actor, true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var pendingObserved = false;
        CombatFlowResult result = CombatFlowResult.Unknown;
        for (var i = 0; i < 400; i++)
        {
            var step = await runner.StepAsync(default);
            pendingObserved |= runner.HasPendingConfirmation;
            if (step.RoundCompleted) { result = step.Result; break; }
        }
        Assert.True(pendingObserved);
        Assert.Equal(2, io.Inputs.Count);
        Assert.Equal(CombatFlowResult.Succeeded, result);
        Assert.Equal(2, io.SkillRequests.Distinct().Count());
        Assert.Equal(1, runner.Context.Find("恢复记录")!.Generation);
    }

    [Fact]
    public async Task TwoIgnoredNativePulsesStillFailWithoutExtendingTheOriginalAttempt()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("班尼特 q(required)");
        using var io = new PhysicalReplay(clock, false, 20, program)
            { Actors = [new("班尼特", 1)], DropAllSkills = true, CompleteSkillReceipts = true };
        io.SetFrontActor("班尼特");
        io.ScheduleEnergy(0, "班尼特", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        Assert.Equal(CombatFlowResult.Failed, await runner.RunRoundAsync(default));
        Assert.Equal(2, io.Inputs.Count);
        Assert.InRange(runner.Context.Now - io.Inputs[0].At, 8, 9);
    }
    [Theory]
    [InlineData("E", "E")]
    [InlineData("e", "E")]
    [InlineData("vk_e", "VK_E")]
    public async Task PairedRawEHoldsForItsDeclaredWaitWithoutSelectionRelease(string downKey, string upKey)
    {
        var clock = new FakeTimeProvider();
        var commands = CombatScriptParser.ParseLineCommands($"keydown({downKey}),wait(0.51),keyup({upKey})", "枫原万叶");
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 attack(0.1)"))
        { Actors = [new("枫原万叶", 1)] };
        io.SetFrontActor("枫原万叶");
        using var runner = NativeCombatFlowRunner.Create(commands, io, loop: false);
        for (var i = 0; i < 300 && io.KeyEvents.All(x => !x.Up); i++) await runner.StepAsync(default);
        var down = Assert.Single(io.KeyEvents.Where(x => !x.Up && x.Key == User32.VK.VK_E));
        var up = io.KeyEvents.First(x => x.Up && x.Key == User32.VK.VK_E);
        Assert.True(up.At - down.At >= .51, $"E held only {up.At - down.At:F3}s");
        Assert.DoesNotContain(io.KeyEvents, x => x.ReleaseAll && x.At > down.At && x.At < down.At + .51);
    }

    [Fact]
    public async Task HeldEAtTwentyFpsReusesInitialPermissionAndReleasesAtFinalPartialSlice()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 0, LoadProgram("琴 attack(0.1)"))
            { Actors = [new("枫原万叶", 1)], NonBlockingHeldCapture = true };
        io.SetFrontActor("枫原万叶");
        using var runner = NativeCombatFlowRunner.Create(
            CombatScriptParser.ParseLineCommands("keydown(E),wait(.51),keyup(E)", "枫原万叶"), io, false);
        var result = await runner.RunRoundAsync(default);
        Assert.Equal(CombatFlowResult.Succeeded, result);
        var down = Assert.Single(io.KeyEvents.Where(x => !x.Up));
        var up = Assert.Single(io.KeyEvents.Where(x => x.Up));
        // 原等待接口按整数毫秒向上取整，最多一个毫秒而不是等下一张50ms帧。
        Assert.InRange(up.At - down.At, .51 - .000001, .511 + .000001);
        Assert.Empty(io.Selections);
    }

    [Theory]
    [InlineData("actor")]
    [InlineData("control")]
    [InlineData("mapping")]
    [InlineData("cancel")]
    public async Task PairedRawELosesOwnershipWithoutSelectingOrReplaying(string failure)
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var commands = CombatScriptParser.ParseLineCommands("keydown(E),wait(1.5),keyup(E)", "枫原万叶");
        PhysicalReplay? replay = null;
        using var io = replay = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(0.1)"))
        {
            Actors = [new("枫原万叶", 1), new("琴", 2)],
            HeldMapping = () => failure == "mapping" && replay!.KeyEvents.Count > 0 ? User32.VK.VK_R : User32.VK.VK_E,
            ControlOverride = _ => new(MotionStatus.Unknown, failure == "control" && replay!.KeyEvents.Count > 0)
        };
        io.SetFrontActor("枫原万叶");
        io.AfterCapture = () =>
        {
            if (io.KeyEvents.Count == 0) return;
            if (failure == "actor") io.SetFrontActor("琴");
            if (failure == "cancel") cancellation.Cancel();
        };
        using var runner = NativeCombatFlowRunner.Create(commands, io, loop: false);
        for (var i = 0; i < 300 && io.KeyEvents.All(x => !x.Up); i++)
        {
            var error = await Record.ExceptionAsync(async () => await runner.StepAsync(cancellation.Token));
            if (error != null) break;
        }
        runner.Dispose();
        Assert.Single(io.KeyEvents.Where(x => !x.Up));
        Assert.Contains(io.KeyEvents, x => x.Up && x.Key == User32.VK.VK_E);
        Assert.Empty(io.Selections);
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData("frozen")]
    [InlineData("future")]
    [InlineData("restart")]
    [InlineData("foreign-actor")]
    [InlineData("no-hud")]
    public async Task PairedRawERejectsInvalidHeldFrames(string fault)
    {
        var clock = new FakeTimeProvider();
        var logger = new SourceIdentityLogger();
        using var io = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(0.1)"))
            { Actors = [new("枫原万叶", 1)], HeldFrameFault = fault, Logger = logger };
        io.SetFrontActor("枫原万叶");
        using var runner = NativeCombatFlowRunner.Create(
            CombatScriptParser.ParseLineCommands("keydown(E),wait(1.5),keyup(E)", "枫原万叶"), io, false);
        for (var i = 0; i < 300 && io.KeyEvents.All(x => !x.Up); i++) await runner.StepAsync(default);
        Assert.Single(io.KeyEvents.Where(x => !x.Up));
        Assert.Contains(io.KeyEvents, x => x.Up);
        Assert.Empty(io.Selections);
        Assert.False(io.HoldingInput);
        runner.Dispose();
        Assert.Contains(logger.Messages, message => message.Contains("held-e:"));
    }

    [Theory]
    [InlineData("mapping")]
    [InlineData("partial")]
    [InlineData("unknown")]
    public async Task PairedRawERequiresSafeMappingAndCompleteNativeReceipt(string fault)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(0.1)"))
        {
            Actors = [new("枫原万叶", 1)],
            HeldMapping = () => fault == "mapping" ? null : User32.VK.VK_E,
            HeldReceipt = fault
        };
        io.SetFrontActor("枫原万叶");
        using var runner = NativeCombatFlowRunner.Create(
            CombatScriptParser.ParseLineCommands("keydown(E),wait(.51),keyup(E)", "枫原万叶"), io, false);
        for (var i = 0; i < 100; i++)
        {
            var error = await Record.ExceptionAsync(async () => await runner.StepAsync(default));
            if (error != null || io.KeyEvents.Any(x => x.Up)) break;
        }
        Assert.True(io.KeyEvents.Count(x => !x.Up) <= 1);
        Assert.False(io.HoldingInput);
        Assert.Empty(io.Selections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HeldEDoesNotSurviveKeepWithdrawalOrItsOriginalDeadline(bool deadline)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("""
            record(许可,duration=20)
            segment(start,name=持键,atomic,timeout=8,required)
            枫原万叶 keydown(E,required),wait(1.5,keep=许可,required),keyup(E,required)
            segment(end,record=持键完成)
            """);
        using var io = new PhysicalReplay(clock, false, 20, program) { Actors = [new("枫原万叶", 1)] };
        io.SetFrontActor("枫原万叶");
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var revoked = false;
        io.AfterCapture = () =>
        {
            if (revoked || io.KeyEvents.Count == 0) return;
            revoked = true;
            if (deadline) io.DelayAsync(9000, default).GetAwaiter().GetResult();
            else runner.Context.TryRecord("许可", runner.Context.Now - .01, .001);
        };
        var result = await runner.RunRoundAsync(default);
        Assert.True(revoked);
        Assert.Equal(CombatFlowResult.Failed, result);
        Assert.Null(runner.Context.Find("持键完成"));
        Assert.Single(io.KeyEvents.Where(x => !x.Up));
        Assert.Contains(io.KeyEvents, x => x.Up);
        Assert.False(io.HoldingInput);
        Assert.Empty(io.Selections);
    }

    [Fact]
    public async Task HeldEReleaseFailureIsNotReportedAsSuccessfulCompletion()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(.1)"))
            { Actors = [new("枫原万叶", 1)] };
        io.SetFrontActor("枫原万叶");
        using var runner = NativeCombatFlowRunner.Create(
            CombatScriptParser.ParseLineCommands("keydown(E),wait(.51),keyup(E)", "枫原万叶"), io, false);
        io.AfterCapture = () => { if (io.KeyEvents.Count > 0) io.InputReleaseError = new IOException("release failed"); };
        try
        {
            Assert.NotNull(await Record.ExceptionAsync(async () => await runner.RunRoundAsync(default)));
            Assert.Null(io.InputCoordinator.TryAcquire(Guid.NewGuid(), () => { }));
            Assert.Single(io.KeyEvents.Where(x => !x.Up));
        }
        finally { io.AfterCapture = null; io.InputReleaseError = null; runner.Dispose(); }
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task DirectAdapterDisposeAfterHeldEDownRetiresFramesEvenWhenPhysicalReleaseFails()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 20, LoadProgram("琴 attack(.1)"))
            { Actors = [new("枫原万叶", 1)] };
        io.SetFrontActor("枫原万叶");
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var context = new CombatFlowContext(clock);
        var action = new CombatFlowAction(new CombatCommand("枫原万叶", "keydown(E)"), context,
            () => true, 8, inAtomicScope: true, heldESpanId: Guid.NewGuid());
        var failure = new IOException("held physical release failed");
        try
        {
            CombatFlowResult result = CombatFlowResult.Unknown;
            for (var i = 0; i < 100 && result != CombatFlowResult.Succeeded; i++)
            {
                adapter.BeginStep();
                if (adapter.HasObservationRequest) await adapter.AdvanceObservationAsync(default);
                else result = await adapter.ExecuteAsync(action, default);
                if (result != CombatFlowResult.Succeeded) await adapter.YieldAsync(default);
            }
            Assert.Equal(CombatFlowResult.Succeeded, result);
            Assert.Single(io.KeyEvents.Where(x => !x.Up));
            Assert.Contains(io.CapturedFrames, frame => !frame.SrcMat.IsDisposed);
            io.InputReleaseError = failure;
            Assert.Same(failure, Record.Exception(() => ((IDisposable)adapter).Dispose()));
            Assert.Null(io.InputCoordinator.TryAcquire(Guid.NewGuid(), () => { }));
            Assert.All(io.CapturedFrames, frame => Assert.True(frame.SrcMat.IsDisposed));
        }
        finally
        {
            io.InputReleaseError = null;
            ((IDisposable)adapter).Dispose();
            io.ReleaseInput();
            // The red regression must not itself retain unmanaged test images.
            foreach (var frame in io.CapturedFrames)
                if (!frame.SrcMat.IsDisposed) frame.Dispose();
        }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeMaintenanceCanTakeOverWhenItBecomesDueDuringSelectionAdmission(bool json)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("""
            strategy(loop=battle)
            timing(盾,cd=12,duration=20)
            钟离 e(hold,wait,timing=盾,record=护盾,maintain=护盾,watch=护盾,before=4,required)
            钟离 wait(13)
            琴 attack(0.1)
            """);
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = json ? NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Info = new() { Declarations = ["""
                timing(盾,cd=12,duration=20)
                segment(主体,define) {
                    钟离 e(hold,wait,timing=盾,record=护盾,maintain=护盾,watch=护盾,before=4,required)
                    钟离 wait(13)
                    琴 attack(0.1)
                }
                """] },
            Actions = [new() { Character = "钟离", Action = "strategy(loop=battle),call(主体,required)" }]
        }, NativeCombatFlowRunner.CreateAdapter(io), clock: clock)!
            : NativeCombatFlowRunner.Create(program, io);
        var crossed = false;
        io.BeforeSelectionInput = () =>
        {
            if (crossed || runner.Context.Find("护盾") == null) return;
            crossed = true;
            io.DelayAsync(2100, default).GetAwaiter().GetResult();
        };
        for (var i = 0; i < 1000 && runner.Context.Now < 22 && io.Inputs.Count < 2; i++)
            await runner.StepAsync(default);
        Assert.True(crossed);
        Assert.Equal(2, io.Inputs.Count);
        Assert.All(io.Inputs, input => Assert.Equal("钟离", input.Actor));
        Assert.DoesNotContain(4, io.Selections);
        Assert.DoesNotContain(io.Primitives, command => command.Method == Method.Attack);
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionObservationYieldsToMaintenanceWithoutSendingOrFailingTheBattle(bool duringSubmission)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 attack(0.1)"));
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var owner = (IDisposable)adapter;
        using var context = new CombatFlowContext(clock);
        var maintenanceDue = false;
        var action = new CombatFlowAction(new CombatCommand("琴", "attack(0.1)"), context, () => true,
            context.Now + 8, shouldYield: () => maintenanceDue);
        adapter.BeginStep();
        Assert.Equal(CombatObservationPreparation.AwaitingObservation,
            await adapter.PrepareObservationStepAsync(action, "onfield", default));
        if (duringSubmission) io.BeforeSelectionInput = () => maintenanceDue = true;
        else maintenanceDue = true;
        Assert.True(action.CanStart);
        Assert.Equal(duringSubmission, action.CanContinue);
        await adapter.AdvanceObservationAsync(default);
        if (duringSubmission)
        {
            adapter.BeginStep();
            await adapter.AdvanceObservationAsync(default);
        }
        Assert.Empty(io.Selections);
        Assert.Empty(io.Inputs);
        Assert.False(io.HoldingInput);
        Assert.False(adapter.HasObservationRequest);
        Assert.Null(action.EffectiveInputAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceRetirementKeepsSubmittedSelectionFactsAndUnknownDeadline(bool unknown)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 attack(0.1)"))
        { UnknownSwitchOutcome = unknown, IgnoreSwitchUntil = 100 };
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var owner = (IDisposable)adapter;
        using var context = new CombatFlowContext(clock);
        var due = false;
        var action = new CombatFlowAction(new CombatCommand("琴", "attack(0.1)"), context, () => true,
            context.Now + 4, shouldYield: () => due);
        adapter.BeginStep();
        await adapter.PrepareObservationStepAsync(action, "onfield", default);
        await adapter.AdvanceObservationAsync(default);
        Assert.Single(io.Selections);
        due = true;
        Exception? failure = null;
        for (var i = 0; i < 100 && adapter.HasObservationRequest && failure == null; i++)
        {
            await io.DelayAsync(50, default);
            adapter.BeginStep();
            failure = await Record.ExceptionAsync(async () => await adapter.AdvanceObservationAsync(default));
        }
        Assert.Single(io.Selections);
        Assert.Empty(io.Inputs);
        Assert.Null(action.EffectiveInputAt);
        if (unknown)
        {
            Assert.IsType<CombatNotFinishedException>(failure);
            Assert.InRange(context.Now, 4, 4.15);
        }
        else
        {
            Assert.Null(failure);
            Assert.False(adapter.HasObservationRequest);
            Assert.True(context.Now < 4);
        }
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("input-error")]
    [InlineData("release-error")]
    public async Task SelectionObservationStillPropagatesCancellationAndRealFailures(string fault)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 attack(0.1)"));
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var owner = (IDisposable)adapter;
        using var context = new CombatFlowContext(clock);
        using var cancellation = new CancellationTokenSource();
        var due = false;
        var action = new CombatFlowAction(new CombatCommand("琴", "attack(0.1)"), context, () => true,
            context.Now + 4, shouldYield: () => due);
        adapter.BeginStep();
        await adapter.PrepareObservationStepAsync(action, "onfield", cancellation.Token);
        io.BeforeSelectionInput = () =>
        {
            if (fault == "cancel") cancellation.Cancel();
            else if (fault == "input-error") throw new IOException("native-input-fault");
            else { due = true; io.InputReleaseError = new IOException("release-fault"); }
        };
        var error = await Record.ExceptionAsync(async () => await adapter.AdvanceObservationAsync(cancellation.Token));
        io.InputReleaseError = null;
        if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<IOException>(error);
        Assert.Empty(io.Selections);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task ReengagementHardDeadlineStopsASingleNativeAtomicWaitAndReleasesHeldInput()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var program = LoadProgram("call(宏,required)\nsegment(宏,define,atomic,timeout=60,record=完成) { 那维莱特 keydown(VK_LBUTTON), wait(1.6), moveby(10,0), keyup(VK_LBUTTON) }");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new HostDevice(clock)
        { AdvanceClock = ms => physical.DelayAsync(ms, default).GetAwaiter().GetResult() };
        var reads = 0;
        var native = new NativeCombatBattleHostIo(flow, device,
            partyObservation: () =>
            {
                var stamp = physical.Producer.Next();
                return new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, false, (ulong)stamp.Sequence) { Source = stamp };
            },
            targetObservation: () => new(physical.Producer.Next(), flow.Context.BattleId, CombatObservationQuality.Available,
                reads++ == 0 ? new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                    new(700, 400, 80, 5, 400), 1, SeekCueKind.HealthBar) : null, 1920, 1080));
        using var host = new CombatBattleHost(native, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 1500 && host.State != "Reengaging"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Reengaging", host.State);
        for (var i = 0; i < 50 && !physical.HoldingInput; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.True(physical.HoldingInput);
        Assert.True(flow.IsAtomic);
        await physical.DelayAsync((int)Math.Round((44.9 - clock.GetElapsedTime(started).TotalSeconds) * 1000), default);
        var before = physical.Primitives.Count;
        var result = await host.AdvanceAsync(flow, default);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 45, 45.051);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Equal(before, physical.Primitives.Count);
        Assert.False(physical.HoldingInput);
    }

    [Fact]
    public async Task NativeHostReengagementResumesTheSameInputOwnerAndStillHonorsCancellation()
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("那维莱特 attack(0.1),check");
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("那维莱特 attack(0.1),check"));
        using var flow = NativeCombatFlowRunner.Create(script.CombatCommands, physical, true);
        var device = new HostDevice(clock)
        { AdvanceClock = ms => physical.DelayAsync(ms, default).GetAwaiter().GetResult() };
        var targetReads = 0;
        var native = new NativeCombatBattleHostIo(flow, device,
            partyObservation: () =>
            {
                var source = physical.Producer.Next();
                return new(source.Sequence, source.CapturedAt, 1920, 1080, false, (ulong)source.Sequence) { Source = source };
            },
            targetObservation: () =>
            {
                var source = physical.Producer.Next();
                EnemySeekDecision? target = targetReads++ == 0
                    ? new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 5, 400), 1, SeekCueKind.HealthBar)
                    : null;
                return new(source, flow.Context.BattleId, CombatObservationQuality.Available, target, 1920, 1080);
            });
        using var host = new CombatBattleHost(native, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 1500 && host.State != "Reengaging"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Reengaging", host.State);
        var before = physical.Primitives.Count;
        for (var i = 0; i < 100 && physical.Primitives.Count == before; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.True(physical.Primitives.Count > before);
        Assert.Equal(Method.Attack, physical.Primitives.Last().Method);
        Assert.Equal(24, device.Inputs.Count(input => input == "camera"));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var count = physical.Primitives.Count;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.AdvanceAsync(flow, cancelled.Token));
        Assert.Equal(count, physical.Primitives.Count);
        Assert.False(physical.HoldingInput);
    }

    [Theory]
    [InlineData("0.3")]
    [InlineData("1.5")]
    public async Task AnonymousPathingAttackObservesFlyingActorWithoutSwitching(string seconds)
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext($"attack({seconds})", false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        { ObservedMotion = MotionStatus.Fly };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        var attack = Assert.Single(io.Primitives);
        Assert.Equal(Method.Attack, attack.Method);
        Assert.Equal(seconds, Assert.Single(attack.Args!));
        Assert.Empty(io.Selections);
        var observations = io.ActiveObservations.Distinct().ToArray();
        Assert.True(observations.Length >= 2);
        Assert.All(observations, item => Assert.Equal(3, item.Index));
        Assert.True(observations[1].Source.IsAfter(observations[0].Source));
    }

    [Fact]
    public async Task AnonymousPathingSpaceWhileFlyingRunsOnceWithoutSelectingAnActor()
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("keypress(VK_SPACE),wait(0.6)", false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        { ObservedMotion = MotionStatus.Fly };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);

        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        var sent = Assert.Single(io.Primitives);
        Assert.Equal(Method.KeyPress, sent.Method);
        Assert.Equal(User32.VK.VK_SPACE, User32Helper.ToVk(Assert.Single(sent.Args!)));
        Assert.Empty(io.Selections);
        Assert.Empty(io.Inputs);
        Assert.True(runner.Context.Now >= .6);
    }

    [Theory]
    [InlineData("named")]
    [InlineData("combat")]
    [InlineData("atomic")]
    [InlineData("climb")]
    [InlineData("control-unknown")]
    [InlineData("stale")]
    [InlineData("repeated")]
    [InlineData("cross-session")]
    [InlineData("actor-changed")]
    [InlineData("cancelled")]
    [InlineData("deadline")]
    public async Task AnonymousPathingAttackCannotBypassObservationBoundaries(string boundary)
    {
        var clock = new FakeTimeProvider();
        var text = boundary switch
        {
            "named" => "那维莱特 attack(0.3)",
            "atomic" => "call(flight,required)\nsegment(start,name=flight,atomic)\nattack(0.3)\nsegment(end)",
            _ => "attack(0.3)"
        };
        var script = CombatScriptParser.ParseContext(text, false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        {
            ObservedMotion = boundary == "climb" ? MotionStatus.Climb : MotionStatus.Fly,
            ControlOverride = boundary == "control-unknown" ? _ => default : null,
            InitialFrameFault = boundary is "stale" or "repeated" or "cross-session" ? boundary : null
        };
        using var cancellation = new CancellationTokenSource();
        var captures = 0;
        io.AfterCapture = () =>
        {
            if (++captures != 1) return;
            if (boundary == "actor-changed") io.SetFrontActor("琴");
            if (boundary == "cancelled") cancellation.Cancel();
            if (boundary == "deadline") clock.Advance(TimeSpan.FromSeconds(10));
        };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: boundary == "combat" ? CombatScriptExecutionPurpose.Combat : CombatScriptExecutionPurpose.Pathing);
        CombatFlowResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await runner.RunRoundAsync(cancellation.Token));
        Assert.True(error != null || result != CombatFlowResult.Succeeded);
        Assert.Empty(io.Primitives);
        Assert.Empty(io.Selections);
        Assert.Equal(0, io.ApproachPulses);
    }

    [Theory]
    [InlineData("climb")]
    [InlineData("unknown")]
    [InlineData("actor")]
    [InlineData("session")]
    [InlineData("cancelled")]
    public async Task AnonymousPathingAttackRechecksAdmissionAfterSelectionReady(string boundary)
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("attack(0.3)", false);
        var readyReleased = false;
        using var cancellation = new CancellationTokenSource();
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        {
            ControlOverride = _ => readyReleased && boundary == "unknown" ? default :
                new(readyReleased && boundary == "climb" ? MotionStatus.Climb : MotionStatus.Fly, false)
        };
        io.AfterRelease = () =>
        {
            if (readyReleased) return;
            readyReleased = true;
            clock.Advance(TimeSpan.FromMilliseconds(250));
            if (boundary == "actor") io.SetFrontActor("琴");
            if (boundary == "session") io.Producer.Restart();
            if (boundary == "cancelled") cancellation.Cancel();
        };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        CombatFlowResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await runner.RunRoundAsync(cancellation.Token));
        Assert.True(readyReleased);
        Assert.True(error != null || result != CombatFlowResult.Succeeded);
        Assert.Empty(io.Primitives);
        Assert.Empty(io.Selections);
    }

    [Theory]
    [InlineData("named")]
    [InlineData("combat")]
    [InlineData("atomic")]
    [InlineData("stale")]
    [InlineData("control-unknown")]
    [InlineData("cancelled")]
    public async Task PathingSpaceDoesNotBypassOtherAdmissionBoundaries(string boundary)
    {
        var clock = new FakeTimeProvider();
        var text = boundary switch
        {
            "named" => "那维莱特 keypress(VK_SPACE)",
            "atomic" => "call(flight,required)\nsegment(start,name=flight,atomic)\nkeypress(VK_SPACE)\nsegment(end)",
            _ => "keypress(VK_SPACE),wait(0.6)"
        };
        var script = CombatScriptParser.ParseContext(text, false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        {
            ObservedMotion = MotionStatus.Fly,
            InitialFrameFault = boundary == "stale" ? "stale" : null,
            ControlOverride = boundary == "control-unknown" ? _ => default : null
        };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: boundary == "combat" ? CombatScriptExecutionPurpose.Combat : CombatScriptExecutionPurpose.Pathing);
        using var cancellation = new CancellationTokenSource();
        if (boundary == "cancelled") cancellation.Cancel();
        CombatFlowResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await runner.RunRoundAsync(cancellation.Token));
        Assert.True(error != null || result != CombatFlowResult.Succeeded);
        if (boundary == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Empty(io.Primitives);
        Assert.Empty(io.Inputs);
        Assert.Empty(io.Selections);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EstablishedSelectionCapturesUnknownOrBreakoutControlBeforeItsEarlyReturn(bool breakout)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var program = LoadProgram("那维莱特 e(required)");
        using var io = new PhysicalReplay(clock, false, 50, program)
        {
            ControlOverride = at => at < .15 ? new(MotionStatus.Unknown, false) :
                breakout ? new(MotionStatus.Unknown, true) : default
        };
        io.SetFrontActor("那维莱特");
        using (var runner = NativeCombatFlowRunner.Create(program, io))
            await Record.ExceptionAsync(async () => await runner.RunRoundAsync(default));
        await evidence.DisposeAsync();
        Assert.Empty(io.Selections);
        Assert.Empty(io.Inputs);
        Assert.Contains(saved, item => item.Phase == "blocked-before-input" && item.Source.IsKnown &&
            item.Detail.Contains(breakout ? "keyboardBreakout=True" : "controlObserved=False"));
    }

    [Fact]
    public async Task UnconfirmedShortSkillEvidenceSeparatesInputContextFromPostInputReadiness()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("钟离 e");
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e")) { DropFirstSkill = true };
        io.SetFrontActor("钟离");
        using (var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false, purpose: CombatScriptExecutionPurpose.Pathing))
            Assert.Equal(CombatFlowResult.Failed, await runner.RunRoundAsync(default));
        await evidence.DisposeAsync();
        Assert.Single(io.Inputs);
        Assert.Contains(saved, item => item.Phase == "before-skill" && item.Detail.Contains("hold=False") &&
            item.Detail.Contains("purpose=Pathing") && item.Detail.Contains("motion=Unknown") && item.Detail.Contains("controlObserved=True"));
        Assert.Contains(saved, item => item.Phase == "deadline" && item.Detail.Contains("cooling=False") && item.Detail.Contains("ready=True"));
    }

    [Theory]
    [InlineData(MotionStatus.Climb)]
    [InlineData(MotionStatus.Fly)]
    public async Task BlockedSelectionBeforeAnyInputStillCapturesItsControlReasonAndSource(MotionStatus motion)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e(required)")) { ObservedMotion = motion };
        using (var runner = NativeCombatFlowRunner.Create(LoadProgram("琴 e(required)"), io))
            Assert.Equal(CombatFlowResult.Failed, await runner.RunRoundAsync(default));
        await evidence.DisposeAsync();
        Assert.Empty(io.Selections);
        Assert.Empty(io.Inputs);
        Assert.Contains(saved, item => item.Phase == "blocked-before-input" && item.Detail.Contains($"motion={motion}") &&
            item.Detail.Contains("submitted=False") && item.Source.IsKnown);
    }

    [Fact]
    public void NativeTxtAndJsonEntryLogsIdentifyTheirOwnLoadedSource()
    {
        var textPath = Path.Combine(Path.GetTempPath(), $"bgi-txt-{Guid.NewGuid():N}.txt");
        var jsonPath = Path.Combine(Path.GetTempPath(), $"bgi-json-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(textPath, "那维莱特 e");
            File.WriteAllText(jsonPath, "{\"info\":{\"name\":\"fixture\"},\"actions\":[{\"character\":\"那维莱特\",\"action\":\"e\"}]}");
            var clock = new FakeTimeProvider();
            var logger = new SourceIdentityLogger();
            var script = CombatScriptParser.Parse(textPath);
            using var game = new PhysicalReplay(clock, false, 50, LoadProgram("那维莱特 e")) { Logger = logger };
            using var textRunner = NativeCombatFlowRunner.Create(CombatFlowProgram.Compile(script), game);
            var strategy = JsonCombatStrategyParser.ParseFile(jsonPath);
            using var jsonRunner = NativeCombatFlowRunner.Create(strategy, NativeCombatFlowRunner.CreateAdapter(game), clock: clock)!;
            var textLog = Assert.Single(logger.Messages.Where(message => message.Contains("format=TXT")));
            var jsonLog = Assert.Single(logger.Messages.Where(message => message.Contains("format=JSON")));
            Assert.Contains(textPath, textLog);
            Assert.Contains(script.CombatCommands[0].SourceTextSha256!, textLog);
            Assert.Contains(textRunner.Context.BattleId.ToString(), textLog);
            Assert.Contains(jsonPath, jsonLog);
            Assert.Contains(strategy.SourceTextSha256!, jsonLog);
            Assert.Contains(jsonRunner.Context.BattleId.ToString(), jsonLog);
            Assert.DoesNotContain(jsonPath, textLog);
            Assert.DoesNotContain(textPath, jsonLog);
        }
        finally { File.Delete(textPath); File.Delete(jsonPath); }
    }

    private sealed class SourceIdentityLogger : ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task NativeHostRecoversTheSkillAxisAfterTargetLossAndEvidenceDoesNotChangeInputs()
    {
        async Task<(List<(string Actor, Method Skill, double At)> Inputs, int Images)> Run(bool captureEvidence)
        {
            var clock = new FakeTimeProvider();
            var program = LoadProgram("那维莱特 e(fast),q(if=q-ready(那维莱特)),attack(0.1)");
            using var game = new PhysicalReplay(clock, true, 50, program);
            using var flow = NativeCombatFlowRunner.Create(new JsonCombatStrategy
            {
                Actions = [new() { Character = "那维莱特", Action = "e(fast),q(if=q-ready(那维莱特)),attack(0.1)" }]
            }, NativeCombatFlowRunner.CreateAdapter(game), clock: clock)!;
            var source = new CaptureFrameSource(clock);
            long? cameraAt = null;
            var device = new HostDevice(clock)
            {
                AfterCamera = () => cameraAt = clock.GetTimestamp(),
                AdvanceClock = ms => game.DelayAsync(ms, default).GetAwaiter().GetResult()
            };
            var saved = new List<DiagnosticEvidence>();
            await using var evidence = captureEvidence
                ? new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; }) : null;
            CombatBattleObservation Target()
            {
                var stamp = source.Next();
                using var pixels = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = stamp };
                evidence?.CaptureRequestedFrames(flow.Context.BattleId.ToString("N"), pixels);
                var settled = cameraAt.HasValue && clock.GetElapsedTime(cameraAt.Value).TotalMilliseconds >= 350;
                return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
                    settled ? new EnemySeekDecision(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                        new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber) : null,
                    1920, 1080, (ulong)(flow.Context.Now * 10) + 1);
            }
            var native = new NativeCombatBattleHostIo(flow, device,
                () => { var stamp = source.Next(); return new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, false, 0) { Source = stamp }; }, Target);
            using var host = new CombatBattleHost(native, new() { FinishCheckIntervalSeconds = .1 });
            for (var i = 0; i < 3000 && flow.Context.Now < 28; i++)
                Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
            Assert.NotNull(cameraAt);
            Assert.Contains(game.Inputs, input => input.Skill == Method.Skill && input.At > 15);
            Assert.Contains(game.Inputs, input => input.Skill == Method.Burst);
            Assert.Single(device.Inputs.Where(input => input == "camera"));
            if (evidence != null) await evidence.DisposeAsync();
            return (game.Inputs.ToList(), saved.Count);
        }
        var plain = await Run(false);
        var recorded = await Run(true);
        Assert.Equal(plain.Inputs, recorded.Inputs);
        Assert.True(recorded.Images > 0);
    }

    [Fact]
    public async Task ActualAdaptiveMiningCannotEnterItsSpecialOperationBeforeSelectionIsConfirmed()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("莉奈娅 wait(0)"))
        { Actors = [new("那维莱特", 1), new("莉奈娅", 2)], IgnoreSwitchUntil = 99 };
        var handler = new LinneaMiningHandler(io);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => handler.RunAsync(default));
        Assert.Contains("专用操作选角未完成", error.Message);
        Assert.Empty(io.Primitives);
        Assert.Empty(io.Inputs);
        Assert.False(io.HoldingInput);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.RunAsync(cancellation.Token));
    }

    [Fact]
    public async Task ActualNahidaHandlerKeepsItsDpiScaledScanAndWaitsForTheSkillEffect()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("纳西妲 e(hold)"))
        { Actors = [new("纳西妲", 1)], DpiScale = 1.5 };
        io.SetFrontActor("纳西妲");
        await new NahidaCollectHandler(io).RunAsync(default);
        Assert.Equal("纳西妲", Assert.Single(io.Inputs).Actor);
        var moves = io.Primitives.Where(command => command.Method == Method.MoveBy).Select(command => string.Join(",", command.Args!)).ToArray();
        Assert.Equal(76, moves.Length);
        Assert.Equal("0,10000", moves[0]);
        Assert.Equal(15, moves.Count(move => move == "600,500"));
        Assert.Equal(19, moves.Count(move => move == "600,-45"));
        Assert.Equal(41, moves.Count(move => move == "600,-75"));
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualPickupHandlerKeepsItsAimSequenceAndRequiresTheSkillEffect(bool ignoredSkill)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e")) { DropFirstSkill = ignoredSkill };
        var handler = new PickUpCollectHandler(io);
        if (ignoredSkill) await Assert.ThrowsAsync<InvalidOperationException>(() => handler.RunAsync(default));
        else await handler.RunAsync(default);
        Assert.Equal("琴", Assert.Single(io.Inputs).Actor);
        Assert.Contains(io.Primitives, command => command.Method == Method.KeyDown && command.Args![0] == "E");
        Assert.Contains(io.Primitives, command => command.Method == Method.MoveBy && command.Args!.SequenceEqual(new[] { "1000", "-3500" }));
        Assert.Equal(3, io.Primitives.Count(command => command.Method == Method.MoveBy && command.Args!.SequenceEqual(new[] { "1000", "0" })));
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualSimpleRouteHandlersKeepTheirActionAndStopOnCancellation(bool skill)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("那维莱特 e"));
        IActionHandler handler = skill ? new ElementalSkillHandler(io) : new NormalAttackHandler(io);
        await handler.RunAsync(default);
        if (skill) Assert.Equal(Method.Skill, Assert.Single(io.Inputs).Skill);
        else
        {
            var attack = Assert.Single(io.Primitives);
            Assert.Equal(Method.Attack, attack.Method);
            Assert.Equal("0", Assert.Single(attack.Args!));
        }
        Assert.InRange(clock.GetElapsedTime(io.CapturedFrames[0].FrameStamp.CapturedTimestamp).TotalSeconds, 1, 4);
        var count = io.Inputs.Count + io.Primitives.Count;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.RunAsync(cancellation.Token));
        Assert.Equal(count, io.Inputs.Count + io.Primitives.Count);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task ActualElementalCollectPreservesPartyOrderAndPrefersTheDeclaredNormalAttack()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("芙宁娜 attack(0.1)"));
        await new ElementalCollectHandler(BetterGenshinImpact.GameTask.AutoGeniusInvokation.Model.ElementalType.Hydro, io).RunAsync(default);
        Assert.Empty(io.Inputs);
        var attack = Assert.Single(io.Primitives);
        Assert.Equal("芙宁娜", attack.Name);
        Assert.Equal(Method.Attack, attack.Method);
        Assert.Equal("0.1", Assert.Single(attack.Args!));
    }

    [Fact]
    public async Task ActualElementalCollectHandlerCannotReturnNormallyWhenItsSkillNeverTookEffect()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e")) { DropFirstSkill = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new ElementalCollectHandler(BetterGenshinImpact.GameTask.AutoGeniusInvokation.Model.ElementalType.Anemo, io).RunAsync(default));
        Assert.Equal("琴", Assert.Single(io.Inputs).Actor);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task AStepWithoutASelectionRequestDoesNotDilutePreparationCostSamples()
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("keypress(f)", false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script));
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false, purpose: CombatScriptExecutionPurpose.Pathing);
        await runner.StepAsync(default);
        Assert.Single(io.Primitives);
        Assert.Equal(0, runner.RuntimeStatistics.PreparationCalls);
    }

    [Fact]
    public async Task NativeOuterMetricsIncludePreparationWaitsWithoutInventingUnenteredExecutionSamples()
    {
        var clock = new FakeTimeProvider();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e")) { VisionReadiness = ready.Task };
        using var runner = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), io);
        try
        {
            await runner.StepAsync(default);
            await runner.StepAsync(default);
            var statistics = runner.RuntimeStatistics;
            Assert.Equal(0, statistics.CoreSteps);
            Assert.Equal(2, statistics.Populations["native-step-total-including-wait"].Count);
            Assert.Equal(2, statistics.Populations["native-preparation-including-wait"].Count);
            Assert.False(statistics.Populations.ContainsKey("native-execution-including-wait"));
        }
        finally { ready.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionOnlyFailuresProduceBoundedCorrelatedEvidenceWithoutWaitingForASkill(bool blocked)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e(required)")) { IgnoreSwitchUntil = blocked ? 99 : 0 };
        using (var runner = NativeCombatFlowRunner.Create(LoadProgram("琴 e(required)"), io))
            Assert.Equal(blocked ? CombatFlowResult.Failed : CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        await evidence.DisposeAsync();
        if (!blocked) Assert.Empty(saved);
        else
        {
            Assert.Empty(io.Inputs);
            Assert.InRange(saved.Count, 2, 3);
            Assert.Single(saved.Select(item => item.Request).Distinct());
            Assert.Contains(saved, item => item.Phase == "before-selection");
            Assert.Contains(saved, item => item.Phase == "unconfirmed");
            Assert.All(saved, item => Assert.True(item.Source.IsKnown));
            Assert.True(saved[1].Source.IsAfter(saved[0].Source));
        }
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task ChangedModelRequirementsAreRepreparedWithoutRenewingAnAwaitingActionsDeadline()
    {
        var clock = new FakeTimeProvider();
        var model = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var program = LoadProgram("那维莱特 e(wait,required,timeout=2)");
        using var io = new PhysicalReplay(clock, false, 50, program);
        io.PrimeSkillCooldown("那维莱特", 3);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        while (runner.Context.Now < .6) await runner.StepAsync(default);
        Assert.Single(io.VisionPreparations);
        io.InvalidateVision(model.Task);
        try
        {
            Assert.Equal(CombatFlowResult.AwaitingObservation, (await runner.StepAsync(default)).Result);
            Assert.Equal(2, io.VisionPreparations.Count);
            await io.DelayAsync(1600, default);
            model.SetResult();
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.StepAsync(default));
            Assert.Empty(io.Inputs);
        }
        finally { model.TrySetResult(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ExplicitEntryPreparationUsesTheRealSelectedStrategyWithoutGameInput(bool json, bool burst)
    {
        var clock = new FakeTimeProvider();
        var text = burst ? "琴 q(required)" : "琴 e(required)";
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(text));
        using var runner = json ? NativeCombatFlowRunner.Create(new JsonCombatStrategy
        { Actions = [new() { Character = "琴", Action = burst ? "q(required)" : "e(required)" }] },
            NativeCombatFlowRunner.CreateAdapter(io), clock: clock)!
            : NativeCombatFlowRunner.Create(LoadProgram(text), io);
        await runner.PrepareVisionBeforeEntryAsync(default);
        Assert.Equal(burst, Assert.Single(io.VisionPreparations));
        Assert.Empty(io.Selections);
        Assert.Empty(io.Inputs);
        Assert.Empty(io.Primitives);
        Assert.All(io.CapturedFrames, frame => Assert.True(frame.SrcMat.IsDisposed));
        Assert.Equal(0, runner.Context.InputAttemptRevision);
    }

    [Fact]
    public async Task PendingVisionPreparationYieldsWithoutAwaitingTheModelAndRechecksAFreshFrame()
    {
        var clock = new FakeTimeProvider();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var program = LoadProgram("那维莱特 e(required)");
        using var io = new PhysicalReplay(clock, false, 50, program) { VisionReadiness = ready.Task };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var step = runner.StepAsync(default).AsTask();
        try
        {
            Assert.Same(step, await Task.WhenAny(step, Task.Delay(150)));
            Assert.Equal(CombatFlowResult.AwaitingObservation, (await step).Result);
            Assert.Empty(io.Inputs);
            Assert.Empty(io.Selections);
            var beforeReady = io.CapturedFrames.Last().FrameStamp;
            await io.DelayAsync(400, default);
            ready.SetResult();
            for (var i = 0; i < 100 && io.Inputs.Count == 0; i++) await runner.StepAsync(default);
            Assert.Single(io.Inputs);
            Assert.All(io.FirstInputCooldownSources, source => Assert.True(source.IsAfter(beforeReady)));
            Assert.InRange(io.Inputs[0].At, .4, 5);
        }
        finally
        {
            ready.TrySetResult();
            await step;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownDeliveredSelectionMayUseSameOwnerBoundedMovementToLeaveACollision(bool unknown)
    {
        using var task = TaskExecutionScope.BeginOwned();
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e(required)");
        using var io = new PhysicalReplay(clock, false, 50, program) { SelectionNeedsMovement = true, UnknownSwitchOutcome = unknown };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var native = new NativeCombatBattleHostIo(runner, io.ControlDevice);
        var scene = new SceneReplayIo(clock, runner.Context.BattleId, io.Producer)
        { AlignedTarget = true, SendInput = native.SendAsync };
        using var host = new CombatBattleHost(scene, new() { FinishDetectionEnabled = false });
        var error = await Record.ExceptionAsync(async () =>
        {
            for (var i = 0; i < 300 && runner.Context.Now < 9 && io.Inputs.Count == 0; i++)
                await host.AdvanceAsync(runner, default);
        });
        if (unknown)
        {
            Assert.IsType<CombatNotFinishedException>(error);
            Assert.Empty(io.Inputs);
            Assert.Equal(0, io.ApproachPulses);
            return;
        }
        Assert.Null(error);
        Assert.Equal("琴", Assert.Single(io.Inputs).Actor);
        Assert.InRange(io.ApproachPulses, 1, 12);
        Assert.False(io.HoldingInput);
        Assert.InRange(io.Inputs[0].At, 0, 5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionAssistanceCannotResetItsMovementBudgetOrRepeatUnknownMovement(bool unknownMovement)
    {
        using var task = TaskExecutionScope.BeginOwned();
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e(required)");
        using var io = new PhysicalReplay(clock, false, 50, program)
        { SelectionNeedsMovement = true, IgnoreSwitchUntil = 99, FailApproach = unknownMovement };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var native = new NativeCombatBattleHostIo(runner, io.ControlDevice);
        var scene = new SceneReplayIo(clock, runner.Context.BattleId, io.Producer)
        { AlignedTarget = true, SendInput = native.SendAsync };
        using var host = new CombatBattleHost(scene, new() { FinishDetectionEnabled = false });
        var error = await Record.ExceptionAsync(async () =>
        {
            for (var i = 0; i < 400 && runner.Context.Now < 9; i++) await host.AdvanceAsync(runner, default);
        });
        Assert.Equal(unknownMovement ? 1 : 12, io.ApproachPulses);
        Assert.False(io.HoldingInput);
        Assert.Empty(io.Inputs);
        if (unknownMovement)
        {
            Assert.IsType<CombatNotFinishedException>(error);
            await Assert.ThrowsAsync<CombatNotFinishedException>(async () => await runner.StepAsync(default));
            Assert.Equal(1, io.ApproachPulses);
        }
        else Assert.Null(error);
    }

    [Fact]
    public async Task FreshButPreSwitchAssistanceFrameDefersWithoutLosingTheGoalThenAcceptsPostInputFrame()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e(required)");
        using var io = new PhysicalReplay(clock, false, 50, program) { SelectionNeedsMovement = true, IgnoreSwitchUntil = 99 };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 50 && runner.SelectionAssistance == null; i++) await runner.StepAsync(default);
        var assistance = runner.SelectionAssistance!.Value;
        var old = io.Producer.Next(io.LastSelectionTimestamp - clock.TimestampFrequency * 13 / 1000);
        Assert.True(old.IsFresh(clock, TimeSpan.FromMilliseconds(150)));
        var request = new CombatBattleHostInput(CombatBattleHostInputKind.Approach)
        { RequestId = Guid.NewGuid(), SelectionGoal = assistance.Goal, Source = old, DeadlineTimestamp = assistance.Deadline };
        var native = new NativeCombatBattleHostIo(runner, io.ControlDevice);
        var deferred = await native.SendAsync(request, default);
        Assert.Equal(CombatBattleHostInputStatus.NotSent, deferred.Status);
        Assert.Equal("selection-awaiting-post-input-frame", deferred.Reason);
        Assert.Equal(0, io.ApproachPulses);
        Assert.Equal(assistance, runner.SelectionAssistance);
        var submitted = await native.SendAsync(request with { Source = io.Producer.Next() }, default);
        Assert.Equal(CombatBattleHostInputStatus.Sent, submitted.Status);
        Assert.Equal(1, io.ApproachPulses);
    }

    [Theory]
    [InlineData("goal")]
    [InlineData("session")]
    [InlineData("expired")]
    [InlineData("cancelled")]
    public async Task DeferredAssistanceDoesNotRelaxIdentityDeadlineOrCancellation(string boundary)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e(required)");
        using var io = new PhysicalReplay(clock, false, 50, program) { SelectionNeedsMovement = true, IgnoreSwitchUntil = 99 };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 50 && runner.SelectionAssistance == null; i++) await runner.StepAsync(default);
        var assistance = runner.SelectionAssistance!.Value;
        if (boundary == "expired") await io.DelayAsync(9000, default);
        var source = io.Producer.Next();
        var request = new CombatBattleHostInput(CombatBattleHostInputKind.Approach)
        {
            RequestId = Guid.NewGuid(), SelectionGoal = boundary == "goal" ? Guid.NewGuid() : assistance.Goal,
            Source = boundary == "session" ? source with { SessionId = Guid.NewGuid() } : source,
            DeadlineTimestamp = assistance.Deadline
        };
        var native = new NativeCombatBattleHostIo(runner, io.ControlDevice);
        using var cancellation = new CancellationTokenSource();
        if (boundary == "cancelled")
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await native.SendAsync(request, cancellation.Token));
        }
        else Assert.Equal(CombatBattleHostInputStatus.Failed, (await native.SendAsync(request, default)).Status);
        Assert.Equal(0, io.ApproachPulses);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task AWaitingNativeSkillCannotSuspendTheHostsNoProgressSupervision()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("那维莱特 e(wait,required,timeout=60)");
        using var io = new PhysicalReplay(clock, false, 50, program);
        io.PrimeSkillCooldown("那维莱特", 60);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var scene = new SceneReplayIo(clock, runner.Context.BattleId) { NoProgress = true };
        using var host = new CombatBattleHost(scene, new() { FinishDetectionEnabled = false, SeekEnabled = false });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1200 && runner.Context.Now < 47 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(runner, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.InRange(runner.Context.Now, 45, 46);
        Assert.True(scene.TargetObservations > 100);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task ObservationPreparationRegistersDemandWithoutSendingAnyPhysicalInput()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        var game = NativeCombatFlowRunner.CreateAdapter(io);
        using var owner = (IDisposable)game;
        using var context = new CombatFlowContext(clock);
        var action = new CombatFlowAction(new("琴", "e"), context, () => true, 8);
        game.BeginStep();
        Assert.Null(game.Observe("e-ready", [], "琴"));
        Assert.Equal(CombatObservationPreparation.AwaitingObservation,
            await game.PrepareObservationStepAsync(action, "e-ready", default));
        Assert.Empty(io.Selections);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task AnAdmittedOptionalBurstKeepsItsIntentWhenTheSidebarFlickersDuringSelection()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("strategy(loop=battle)\n琴 q(if=q-ready(琴),record=原Q)\n琴 e(record=后E)");
        using var io = new PhysicalReplay(clock, false, 50, program)
        { HideSideBurstDuringSelection = true };
        io.ScheduleEnergy(0, "琴", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);

        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        Assert.Equal(new[] { Method.Burst, Method.Skill }, io.Inputs.Select(input => input.Skill));
        Assert.NotNull(runner.Context.Find("原Q"));
        Assert.NotNull(runner.Context.Find("后E"));
        Assert.InRange(io.Inputs[0].At, 0, 5);
    }

    [Fact]
    public async Task AnUnknownSwitchThatExpiresCannotBecomeAnOptionalSkipAndSelectAnotherActor()
    {
        using var task = TaskExecutionScope.BeginOwned();
        var clock = new FakeTimeProvider();
        var program = LoadProgram("strategy(loop=battle)\n琴 e\n芙宁娜 e");
        using var io = new PhysicalReplay(clock, false, 50, program)
        { IgnoreSwitchUntil = 99, UnknownSwitchOutcome = true };
        using var runner = NativeCombatFlowRunner.Create(program, io);

        await Assert.ThrowsAsync<CombatNotFinishedException>(async () => await runner.RunRoundAsync(default));
        Assert.Equal(4, Assert.Single(io.Selections));
        Assert.Empty(io.Inputs);
        Assert.InRange(runner.Context.Now, 0, 8.1);
    }

    [Theory]
    [InlineData(.3, 0)]
    [InlineData(1.8, 50)]
    [InlineData(3, 150)]
    public async Task ADeliveredButIgnoredSwitchCanRecoverAndCastWithinItsOriginalWindow(double recoverAt, double cost)
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("钟离 e(required)");
        using var io = new PhysicalReplay(clock, false, cost, LoadProgram("钟离 e(required)"))
        { IgnoreSwitchUntil = recoverAt };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false);

        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        var input = Assert.Single(io.Inputs);
        Assert.Equal("钟离", input.Actor);
        Assert.Equal(Method.Skill, input.Skill);
        Assert.InRange(io.Selections.Count, 2, 10);
        Assert.InRange(runner.Context.Now, recoverAt, 8);
        Assert.True(double.IsFinite(io.FirstShieldConfirmed));
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData("null", true)]
    [InlineData("stale", true)]
    [InlineData("slow", true)]
    [InlineData("null", false)]
    [InlineData("stale", false)]
    [InlineData("slow", false)]
    public async Task InitialObservationHasOneBoundedAdmissionForActualMiningAndPathing(string fault, bool mining)
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext(mining ? "钟离 e(hold,wait)" : "keypress(f)", mining);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        { InitialFrameFault = fault };
        var started = clock.GetTimestamp();
        // 外部采集器的有限录制在10秒结束；不能依靠测试取消代替生产的8秒准入期限。
        io.AfterCapture = () =>
        {
            if (clock.GetElapsedTime(started).TotalSeconds >= 10) throw new InvalidDataException("external recording exhausted");
        };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (mining) await new MiningHandler(io).RunAsync(default);
            else await runner.RunRoundAsync(default);
        });
        Assert.Contains("战斗视觉准备", error.Message);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 8, 8.3);
        Assert.Empty(io.Inputs);
        Assert.Empty(io.Primitives);
        Assert.Empty(io.Selections);
        if (!mining)
        {
            io.InitialFrameFaultUntil = 0;
            Assert.Contains("战斗视觉准备", (await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await runner.StepAsync(default))).Message);
            Assert.Empty(io.Primitives);
        }
    }

    [Theory]
    [InlineData(7.5, true)]
    [InlineData(8.1, false)]
    public async Task InitialObservationRecoveryDoesNotRenewItsAdmissionDeadline(double recoverAt, bool succeeds)
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("keypress(f)", false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        { InitialFrameFault = "null", InitialFrameFaultUntil = recoverAt };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        if (succeeds)
        {
            Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
            Assert.Single(io.Primitives);
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunRoundAsync(default));
            Assert.Empty(io.Primitives);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SkillEvidenceIsPersistedOnlyForUnconfirmedAttemptsAndKeepsOriginalFrames(bool dropped)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(required)")) { DropFirstSkill = dropped };
        io.SetFrontActor("钟离");
        using (var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseContext("钟离 e(required)").CombatCommands, io, false))
            Assert.Equal(dropped ? CombatFlowResult.Failed : CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        await evidence.DisposeAsync();
        if (!dropped) Assert.Empty(saved);
        else
        {
            Assert.InRange(saved.Count, 2, 3);
            Assert.Contains(saved, item => item.Phase == "before-skill");
            Assert.Contains(saved, item => item.Phase == "unconfirmed");
            Assert.Single(saved.Select(item => item.Request).Distinct());
            Assert.All(saved, item => Assert.True(item.Source.IsKnown));
            Assert.True(saved[1].Source.IsAfter(saved[0].Source));
        }
    }

    [Theory]
    [InlineData("s32-cannon-reset.json", false)]
    [InlineData("s32-cannon-activate.json", true)]
    public async Task RecordedCannonJsonScriptNodesCompleteThroughTheProductionRunner(string file, bool fires)
    {
        var root = JObject.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pathing", file)));
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50,
            CombatFlowProgram.Compile(CombatScriptParser.ParseContext("keypress(f)", false)))
        { HideHudAfterInteraction = true, KnownCannonInteraction = true, DelayLatenessMs = 12.5 };
        var nodes = root["positions"]!.Where(node => (string?)node["action"] == "combat_script").ToArray();
        Assert.NotEmpty(nodes);
        foreach (var node in nodes)
        {
            var script = CombatScriptParser.ParseContext((string)node["action_params"]!, false);
            using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
                CombatScriptExecutionMode.LegacyPartyTemplate, purpose: CombatScriptExecutionPurpose.Pathing);
            Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        }
        Assert.Equal(fires, io.Primitives.Any(command => command.Method == Method.KeyPress && command.Args![0] == "RETURN"));
        Assert.Empty(io.Selections);
        Assert.Empty(io.Inputs);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task CannonFireAndExitUseTheSamePathingOwnerWithoutACharacterHud()
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("keypress(f),wait(0.2),keypress(RETURN),wait(0.1),keypress(ESCAPE)", false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        { HideHudAfterInteraction = true, KnownCannonInteraction = true };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        Assert.Equal(new[] { "f", "RETURN", "ESCAPE" }, io.Primitives.Select(command => command.Args![0]));
        Assert.Empty(io.Selections);
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UnknownOrForeignCannonEvidenceDoesNotSendReturnOrRestartItsDeadline(bool known, bool foreign)
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("keypress(f),wait(0.2),keypress(RETURN)", false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        { HideHudAfterInteraction = true, KnownCannonInteraction = known, ForeignCannonSource = foreign };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        Assert.Equal(CombatFlowResult.Failed, await runner.RunRoundAsync(default));
        Assert.Equal("f", Assert.Single(io.Primitives).Args![0]);
        // RunRound还包含已有的1秒失败收尾；以外部最后一次场景读取验证原动作期限。
        Assert.True(io.CannonObservationTimes.Count > 1);
        Assert.InRange(io.CannonObservationTimes[^1] - io.CannonObservationTimes[0], 7.5, 8);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5.1)]
    public async Task ActualMiningHandlerWaitsForAndConfirmsZhongliInsteadOfFallingBack(double cooldown)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(hold,wait)"));
        io.SetFrontActor("钟离");
        io.PrimeSkillCooldown("钟离", cooldown);
        await new MiningHandler(io).RunAsync(default);
        var input = Assert.Single(io.Inputs);
        Assert.Equal("钟离", input.Actor);
        Assert.Equal(Method.Skill, input.Skill);
        Assert.True(input.At >= cooldown);
        Assert.True(double.IsFinite(io.FirstShieldConfirmed));
        Assert.Empty(io.Primitives);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task ActualMiningHandlerCannotCompleteAnUnconfirmedSkillOrRunAnotherMiner()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(hold,wait)")) { DropFirstSkill = true };
        io.SetFrontActor("钟离");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new MiningHandler(io).RunAsync(default));
        Assert.Contains("未确认", error.Message);
        Assert.Single(io.Inputs);
        Assert.Empty(io.Primitives);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task ActualMiningHandlerRejectsAMissingMinerBeforeAnyInput()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("香菱 e"))
        { Actors = [new("香菱", 1)] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new MiningHandler(io).RunAsync(default));
        Assert.Empty(io.Inputs);
        Assert.Empty(io.Primitives);
    }

    [Fact]
    public async Task ActualMiningHandlerPreservesOriginalFailureAndCompletedRecovery()
    {
        var recovered = Assert.IsType<CombatRecoveryCompletedException>(await Record.ExceptionAsync(() =>
            CombatRecoveryCompletedException.RecoverAsync(() => Task.CompletedTask, () => true,
                () => Task.CompletedTask, default)));
        foreach (var expected in new Exception[] { new OperationCanceledException("cancelled"),
                     new CombatNotFinishedException("not finished"), new InvalidOperationException("capture failed"), recovered })
        {
            var clock = new FakeTimeProvider();
            using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(hold,wait)"))
            { AfterCapture = () => throw expected };
            Assert.Same(expected, await Record.ExceptionAsync(() => new MiningHandler(io).RunAsync(default)));
            Assert.Empty(io.Inputs);
            Assert.Empty(io.Primitives);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualMiningHandlerCancellationPreventsPickupAndLaterInput(bool cancelAfterSubmission)
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(hold,wait)"));
        io.SetFrontActor("钟离");
        if (!cancelAfterSubmission) cancellation.Cancel();
        io.AfterCapture = () => { if (io.Inputs.Count > 0) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new MiningHandler(io).RunAsync(cancellation.Token));
        Assert.Equal(cancelAfterSubmission ? 1 : 0, io.Inputs.Count);
        Assert.Empty(io.Primitives);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task ActualPathingWaitKeepsItsOriginalBudgetUnderLateSchedulerWakeups()
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("wait(6.5),keypress(f)", false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        { DelayLatenessMs = 12.5 };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);

        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        Assert.InRange(runner.Context.Now, 6.5, 7);
        Assert.Equal("f", Assert.Single(io.Primitives).Args![0]);
        Assert.Empty(io.Selections);
    }

    [Fact]
    public async Task PathingDeadlineCrossedInsideNativeDispatchCannotBecomeAnOptionalSkip()
    {
        var clock = new FakeTimeProvider();
        var logger = new PausingAdmissionLogger();
        var script = CombatScriptParser.ParseContext("钟离 e");
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e"))
        { SkillUnreadyUntil = 99, Logger = logger };
        logger.Advance = () => io.DelayAsync(9000, default).GetAwaiter().GetResult();
        io.SetFrontActor("钟离");
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        Assert.Equal(CombatFlowResult.AwaitingObservation, (await runner.StepAsync(default)).Result);
        logger.PauseNext = true; // 真实执行端在内核CanStart之后查询日志等级，模拟该接缝被调度暂停。
        Assert.Equal(CombatFlowResult.Failed, await runner.RunRoundAsync(default));
        Assert.Empty(io.Inputs);
    }

    private sealed class PausingAdmissionLogger : ILogger
    {
        public bool PauseNext { get; set; }
        public Action Advance { get; set; } = () => { };
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level)
        {
            if (PauseNext) { PauseNext = false; Advance(); }
            return false;
        }
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> format) { }
    }

    [Fact]
    public async Task PathingUiCannotUseCopiesOfThePreInputFrameToAdvanceOrResetItsDeadline()
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("keypress(f),wait(0.2),keypress(ESCAPE)", false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script))
        { HideHudAfterInteraction = true, RepeatInteractionFrame = true };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        Assert.Equal(CombatFlowResult.Failed, await runner.RunRoundAsync(default));
        Assert.Single(io.Primitives);
        Assert.InRange(runner.Context.Now, 8, 9.3);
    }

    [Fact]
    public async Task CancellingAPathingUiSequenceDrainsItsOwnerWithoutAnotherUiKey()
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var script = CombatScriptParser.ParseContext("keypress(f),wait(0.2),keypress(ESCAPE)", false);
        using var io = new PhysicalReplay(clock, false, 50, CombatFlowProgram.Compile(script)) { HideHudAfterInteraction = true };
        io.AfterCapture = () => { if (io.Primitives.Count > 0) cancellation.Cancel(); };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            purpose: CombatScriptExecutionPurpose.Pathing);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.RunRoundAsync(cancellation.Token));
        Assert.Single(io.Primitives);
        Assert.False(runner.Context.IsOpen);
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData(1d, true)]
    [InlineData(99d, false)]
    public async Task UnknownPathingSkillReadinessWaitsWithinTheOriginalDeadline(double unreadyUntil, bool expectedSuccess)
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("钟离 e");
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e")) { SkillUnreadyUntil = unreadyUntil };
        io.SetFrontActor("钟离");
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            CombatScriptExecutionMode.LegacyPartyTemplate, purpose: CombatScriptExecutionPurpose.Pathing);
        var result = await runner.RunRoundAsync(default);
        Assert.Equal(expectedSuccess ? CombatFlowResult.Succeeded : CombatFlowResult.Failed, result);
        Assert.Equal(expectedSuccess ? 1 : 0, io.Inputs.Count);
        if (expectedSuccess) Assert.InRange(io.Inputs[0].At, unreadyUntil, 8);
        else Assert.InRange(runner.Context.Now, 8, 9.2);
    }

    [Theory]
    [InlineData(1d, true, "e")]
    [InlineData(99d, false, "e")]
    [InlineData(1d, true, "e(fast)")]
    [InlineData(99d, false, "e(fast)")]
    public async Task UnknownCombatSkillReadinessKeepsTheOriginalCommandWithoutReplayingEarlierInput(double unreadyUntil, bool expectedSuccess, string skill)
    {
        var clock = new FakeTimeProvider();
        var logger = new SourceIdentityLogger();
        var script = CombatScriptParser.ParseContext($"钟离 attack(0.1),{skill},attack(0.1)");
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e"))
        { SkillUnreadyUntil = unreadyUntil, Logger = logger };
        io.SetFrontActor("钟离");
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false);
        var result = await runner.RunRoundAsync(default);
        Assert.Equal(expectedSuccess ? CombatFlowResult.Succeeded : CombatFlowResult.Failed, result);
        Assert.Equal(expectedSuccess ? 1 : 0, io.Inputs.Count);
        Assert.Equal(expectedSuccess ? 2 : 1, io.Primitives.Count);
        if (expectedSuccess) Assert.InRange(io.Inputs[0].At, unreadyUntil, 8);
        else Assert.InRange(runner.Context.Now, 8, 9.5);
        runner.Dispose();
        Assert.Contains(logger.Messages, message => message.Contains("skill-readiness") && message.Contains("gate=unknown-readiness") &&
            message.Contains("active=1") && message.Contains("cooldown=unknown"));
    }

    [Theory]
    [InlineData(1d, true)]
    [InlineData(99d, false)]
    public async Task UnknownOptionalEnhancedBurstReadinessReachesTheSharedBoundedObservationGate(double unknownUntil, bool casts)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("那维莱特 q");
        using var io = new PhysicalReplay(clock, true, 50, program) { BurstUnknownUntil = unknownUntil };
        io.SetFrontActor("那维莱特");
        using var runner = NativeCombatFlowRunner.Create(program, io);
        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        if (casts)
        {
            Assert.Equal(Method.Burst, Assert.Single(io.Inputs).Skill);
            Assert.InRange(io.Inputs[0].At, 1, 8);
        }
        else
        {
            Assert.Empty(io.Inputs); // 增强脚本的optional跳过不等于施放完成。
            Assert.InRange(runner.Context.Now, 8, 9.5);
        }
    }

    [Theory]
    [InlineData(2d, true)]
    [InlineData(99d, false)]
    public async Task LegacyBurstAfterConfirmedSkillDoesNotSpendPreparationAttemptsOnEveryUnknownFrame(double unknownUntil, bool casts)
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("那维莱特 e,q,attack(0.1)");
        using var io = new PhysicalReplay(clock, true, 50, LoadProgram("那维莱特 e,q")) { BurstUnknownUntil = unknownUntil };
        io.SetFrontActor("那维莱特");
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false);
        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        Assert.Single(io.Inputs.Where(input => input.Skill == Method.Skill));
        Assert.Equal(casts ? 1 : 0, io.Inputs.Count(input => input.Skill == Method.Burst));
        Assert.Single(io.Primitives.Where(command => command.Method == Method.Attack));
        if (casts) Assert.InRange(io.Inputs.Single(input => input.Skill == Method.Burst).At, unknownUntil, 8);
        else Assert.InRange(runner.Context.Now, 8, 11);
    }

    [Theory]
    [InlineData("required")]
    [InlineData("submitted")]
    [InlineData("actor")]
    [InlineData("control")]
    [InlineData("stale")]
    [InlineData("cancelled")]
    public async Task OptionalBurstTimeoutCannotHideOtherFailuresOrReuseInvalidReadiness(string boundary)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var cancellation = new CancellationTokenSource();
        var text = boundary == "required" ? "那维莱特 q(required),attack(0.1,required)" : "那维莱特 q,attack(0.1)";
        var script = CombatScriptParser.ParseContext(text);
        using var io = new PhysicalReplay(clock, true, 50, LoadProgram(text))
        {
            BurstUnknownUntil = boundary == "submitted" ? 0 : 99,
            DropFirstSkill = boundary == "submitted",
            IgnoreSwitchUntil = boundary == "actor" ? 99 : 0,
            ControlOverride = boundary == "control"
                ? at => at < 2 ? new(MotionStatus.Normal, false) : default
                : null
        };
        io.SetFrontActor(boundary == "actor" ? "琴" : "那维莱特");
        io.AfterCapture = () =>
        {
            if (clock.GetElapsedTime(started).TotalSeconds < 2) return;
            if (boundary == "stale") clock.Advance(TimeSpan.FromMilliseconds(200));
            if (boundary == "cancelled") cancellation.Cancel();
        };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false);
        CombatFlowResult? result = null;
        var error = await Record.ExceptionAsync(async () => result = await runner.RunRoundAsync(cancellation.Token));
        Assert.True(error != null || result != CombatFlowResult.Succeeded);
        if (boundary == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.DoesNotContain(io.Primitives, command => command.Method == Method.Attack);
        if (boundary == "submitted") Assert.Equal(Method.Burst, Assert.Single(io.Inputs).Skill);
        else Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task SkillReadinessDeadlineIsReportedEvenWhenSchedulingSkipsTheLastObservationWindow()
    {
        var clock = new FakeTimeProvider();
        var logger = new SourceIdentityLogger();
        var script = CombatScriptParser.ParseContext("钟离 e");
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e"))
        { SkillUnreadyUntil = 99, Logger = logger };
        io.SetFrontActor("钟离");
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false);
        for (var i = 0; i < 100 && runner.Context.Now < 1; i++) await runner.StepAsync(default);
        await io.DelayAsync(9000, default);
        Assert.Equal(CombatFlowResult.Failed, await runner.RunRoundAsync(default));
        Assert.Empty(io.Inputs);
        Assert.Contains(logger.Messages, message => message.Contains("SKILL_READINESS_END") && message.Contains("phase=deadline"));
        Assert.Contains(logger.Messages, message => message.Contains("EVIDENCE_CAPTURE_MISSING") &&
            message.Contains("phase=deadline") && message.Contains("no-existing-frame"));
    }

    [Fact]
    public async Task CancellingUnknownCombatReadinessDoesNotSubmitTheSkillOrFollowingAction()
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var script = CombatScriptParser.ParseContext("钟离 attack(0.1),e,attack(0.1)");
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e")) { SkillUnreadyUntil = 99 };
        io.SetFrontActor("钟离");
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false);
        io.AfterCapture = () => { if (runner.Context.Now >= 1) cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.RunRoundAsync(cancellation.Token));
        Assert.Single(io.Primitives);
        Assert.Empty(io.Inputs);
        Assert.False(io.HoldingInput);
        Assert.False(runner.Context.IsOpen);
    }

    [Fact]
    public async Task PathingFastSkillStillSkipsKnownCooldownWithoutWaitingOrClaimingACast()
    {
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext("钟离 e(fast)");
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e"));
        io.PrimeSkillCooldown("钟离", 5.1);
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, false,
            CombatScriptExecutionMode.LegacyPartyTemplate, purpose: CombatScriptExecutionPurpose.Pathing);
        Assert.Equal(CombatFlowResult.Skipped, await runner.RunRoundAsync(default));
        Assert.Empty(io.Inputs);
        Assert.True(runner.Context.Now < 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnonymousCannonNavigationCanCrossUiOnlyWhenTheCallerDeclaresPathing(bool pathing)
    {
        const string text = "w(0.5),wait(1),keypress(f),wait(0.2),keypress(f),wait(0.5),keypress(w),wait(0.1),keypress(ESCAPE)";
        var clock = new FakeTimeProvider();
        var script = CombatScriptParser.ParseContext(text, validate: false);
        var program = CombatFlowProgram.Compile(script);
        using var io = new PhysicalReplay(clock, false, 50, program) { HideHudAfterInteraction = true };
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands,
            io, loop: false, CombatScriptExecutionMode.LegacyPartyTemplate,
            purpose: pathing ? CombatScriptExecutionPurpose.Pathing : CombatScriptExecutionPurpose.Combat);
        var result = await runner.RunRoundAsync(default);
        if (pathing)
        {
            Assert.Equal(CombatFlowResult.Succeeded, result);
            Assert.Equal(new[] { "f", "f", "w", "ESCAPE" },
                io.Primitives.Where(command => command.Method == Method.KeyPress).Select(command => command.Args![0]));
            Assert.Empty(io.Selections);
        }
        else
        {
            Assert.NotEqual(CombatFlowResult.Succeeded, result);
            Assert.Single(io.Primitives, command => command.Method == Method.KeyPress);
        }
        Assert.Empty(io.Inputs);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task PlainPathingSkillKeepsItsCursorWhileCooldownExpiresInsteadOfFailingTheWholeRoute()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("钟离 d(0.2),e");
        using var io = new PhysicalReplay(clock, false, 50, program);
        io.SetFrontActor("钟离");
        io.PrimeSkillCooldown("钟离", 5.1);
        using var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseContext("钟离 d(0.2),e").CombatCommands,
            io, loop: false, CombatScriptExecutionMode.LegacyPartyTemplate, purpose: CombatScriptExecutionPurpose.Pathing);
        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        Assert.Single(io.Primitives); // 等CD不能重放前面的侧移。
        Assert.Equal(Method.D, io.Primitives[0].Method);
        var input = Assert.Single(io.Inputs);
        Assert.InRange(input.At, 5.1, 8);
    }

    [Fact]
    public async Task FragmentRoundEntryUsesTheSameControlRecoveryContinuationAsBattleSteps()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e(required)");
        using var io = new PhysicalReplay(clock, false, 50, program) { RecoveryPulsesRequired = 3 };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        Assert.Equal(CombatFlowResult.Succeeded, await runner.RunRoundAsync(default));
        Assert.Equal(3, io.ControlPulses);
        Assert.Single(io.Inputs);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task ASubmittedPrimitiveInterruptedAfterwardCannotCompleteItsAtomicRecord()
    {
        var program = LoadProgram("call(宏,required)\nsegment(宏,define,atomic,record=完成) { 那维莱特 keydown(VK_LBUTTON), wait(0.08), keyup(VK_LBUTTON) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program)
        { PrimitivePostError = new CombatActionInterruptedException() };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var index = 0; index < 100 && !step.RoundCompleted; index++) step = await runner.StepAsync(default);
        Assert.Null(runner.Context.Find("完成"));
        Assert.Equal(CombatFlowResult.Failed, step.Result);
        Assert.Single(io.Primitives);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task PartiallySubmittedPartyCloseIsNotReplayedByCleanup()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var source = new CaptureFrameSource(clock);
        var device = new HostDevice(clock) { NativeReceipts = true, RejectSecondParty = true };
        var native = new NativeCombatBattleHostIo(flow, device, () =>
        {
            var stamp = source.Next();
            return new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, true, (ulong)stamp.Sequence) { Source = stamp };
        });
        CombatBattleHostInput Request(CombatBattleHostInputKind kind, CaptureFrameStamp stamp) => new(kind, PartyEvidence: true)
        { RequestId = Guid.NewGuid(), Source = stamp, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency * 2 };
        Assert.Equal(CombatBattleHostInputStatus.Sent, (await native.SendAsync(Request(CombatBattleHostInputKind.OpenParty, source.Next()), default)).Status);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var page = native.ObservePartyBar();
        var closed = await native.SendAsync(Request(CombatBattleHostInputKind.CloseParty, page.Source), default);
        Assert.Equal(CombatBattleHostInputStatus.Unknown, closed.Status);
        Assert.Equal(4, closed.NativeRequested);
        Assert.Equal(2, closed.NativeSubmitted);
        var before = device.Inputs.ToArray();
        native.ReleaseInput();
        flow.Dispose();
        native.ReleaseInput();
        Assert.Equal(before, device.Inputs);
    }

    [Fact]
    public async Task ExpiredSubmittedSelectionIsRetiredBeforeANewActorCanReplaceIt()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e\n芙宁娜 e"));
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var owner = (IDisposable)adapter;
        using var context = new CombatFlowContext(clock);
        var old = new CombatFlowAction(new("琴", "e"), context, () => true, 1);
        adapter.BeginStep();
        await adapter.PrepareObservationStepAsync(old, "onfield", default);
        await adapter.AdvanceObservationAsync(default);
        Assert.Single(io.Selections);
        await io.DelayAsync(1500, default);
        var next = new CombatFlowAction(new("芙宁娜", "e"), context, () => true, context.Now + 4);
        adapter.BeginStep();
        await adapter.AdvanceObservationAsync(default);
        Assert.NotNull(await Record.ExceptionAsync(async () =>
            await adapter.PrepareObservationStepAsync(next, "onfield", default)));
        Assert.Single(io.Selections);
    }

    [Theory]
    [InlineData("琴 e(required)", false)]
    [InlineData("琴 q(required)", true)]
    public async Task NativePreparationConsumesCompiledModelRequirements(string text, bool needsBurst)
    {
        var program = LoadProgram(text);
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        await runner.StepAsync(default);
        Assert.Equal(new[] { needsBurst }, io.VisionPreparations);
    }

    [Fact]
    public async Task InitialControlIsObservedBeforePreparingTheBurstModel()
    {
        var program = LoadProgram("琴 q(required)");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program) { ControlAt = at => at < 1 };
        io.ScheduleEnergy(0, "琴", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        await runner.StepAsync(default);
        Assert.Empty(io.VisionPreparations);
        Assert.Empty(io.Inputs);
        for (var i = 0; i < 100 && io.Inputs.Count == 0; i++) await runner.StepAsync(default);
        Assert.Equal(new[] { true }, io.VisionPreparations);
        Assert.Single(io.Inputs);
        Assert.Equal(Method.Burst, io.Inputs[0].Skill);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task KeyboardControlRecoveryResumesTheOriginalNativeSkillWithoutRepeatingItsInput(bool json, bool enhanced)
    {
        var clock = new FakeTimeProvider();
        var action = enhanced ? "e(required,record=已施放)" : "e";
        var program = LoadProgram("琴 " + action);
        using var io = new PhysicalReplay(clock, false, 50, program) { RecoveryPulsesRequired = 3 };
        using var runner = json ? NativeCombatFlowRunner.Create(new JsonCombatStrategy
        { Actions = [new() { Character = "琴", Action = action }] },
            NativeCombatFlowRunner.CreateAdapter(io), clock: clock)!
            : NativeCombatFlowRunner.Create(CombatScriptParser.ParseContext("琴 " + action).CombatCommands, io, false);
        bool Confirmed() => runner.RuntimeStatistics.RecentEvents.Any(e => e.Actor == "琴" && e.Action == Method.Skill.Alias[0] && e.ReportedResult == "Succeeded");
        for (var i = 0; i < 160 && runner.Context.Now < 8 && !Confirmed(); i++)
            await runner.StepAsync(default);
        Assert.True(Confirmed());
        if (enhanced) Assert.NotNull(runner.Context.Find("已施放"));
        Assert.Equal(3, io.ControlPulses);
        var input = Assert.Single(io.Inputs);
        Assert.Equal("琴", input.Actor);
        Assert.InRange(input.At, 0, 5);
        Assert.False(io.HoldingInput);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ControlInputUncertaintyOrPersistentNonAdmissionCannotLoopOrSendAgain(bool partial)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e(required)");
        using var io = new PhysicalReplay(clock, false, 50, program)
        { RecoveryPulsesRequired = 20, FailControlPulse = partial, ControlPrepareDelay = partial ? 0 : 292 };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        Exception? failure = null;
        for (var i = 0; i < 100 && failure == null; i++)
            failure = await Record.ExceptionAsync(async () => await runner.StepAsync(default));
        Assert.NotNull(failure);
        Assert.Empty(io.Inputs);
        Assert.InRange(runner.Context.Now, 0, 5);
        var pulses = io.ControlPulses;
        Assert.Equal(partial ? 1 : 0, pulses);
        Assert.NotNull(await Record.ExceptionAsync(async () => await runner.StepAsync(default)));
        Assert.Equal(pulses, io.ControlPulses);
    }

    [Fact]
    public async Task ControlInterruptionKeepsAnAlreadySentSelectionUntilItSettlesBeforeTheNextActorRequest()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e\n芙宁娜 e"))
        { ControlAt = at => at is >= .1 and < .5, UnknownSwitchOutcome = true };
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var owner = (IDisposable)adapter;
        using var context = new CombatFlowContext(clock);
        var old = new CombatFlowAction(new CombatCommand("琴", "e"), context, () => true, context.Now + 4);
        adapter.BeginStep();
        Assert.Equal(CombatObservationPreparation.AwaitingObservation, await adapter.PrepareObservationStepAsync(old, "onfield", default));
        await adapter.AdvanceObservationAsync(default);
        Assert.Single(io.Selections);
        await io.DelayAsync(200, default);
        adapter.BeginStep();
        await adapter.AdvanceObservationAsync(default);
        Assert.Equal(CombatObservationPreparation.AwaitingObservation, await adapter.PrepareObservationStepAsync(old, "onfield", default));
        adapter.CancelObservation(old);
        var next = new CombatFlowAction(new CombatCommand("芙宁娜", "e"), context, () => true, context.Now + 4);
        while (context.Now < .8)
        {
            adapter.BeginStep();
            await adapter.AdvanceObservationAsync(default);
            await adapter.PrepareObservationStepAsync(next, "onfield", default);
            await io.DelayAsync(50, default);
        }
        Assert.Single(io.Selections); // 不得把旧切人丢弃后立即向另一个角色发请求。
        var result = CombatObservationPreparation.AwaitingObservation;
        for (var i = 0; i < 80 && result == CombatObservationPreparation.AwaitingObservation; i++)
        {
            adapter.BeginStep();
            await adapter.AdvanceObservationAsync(default);
            result = await adapter.PrepareObservationStepAsync(next, "onfield", default);
            await io.DelayAsync(50, default);
        }
        Assert.Equal(CombatObservationPreparation.Ready, result);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task ExplicitControlLossInterruptsTheRawMacroWithoutWritingItsCompletionRecord()
    {
        var program = LoadProgram("call(宏,required)\nsegment(宏,define,atomic,record=完成) { 那维莱特 keydown(VK_LBUTTON), wait(1.6), moveby(10,0), keyup(VK_LBUTTON) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program) { FreezeWhileHeld = true };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 40 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Equal(CombatFlowResult.Failed, step.Result);
        Assert.Null(runner.Context.Find("完成"));
        Assert.Single(io.Primitives);
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task NativeHostFirstCheckDelayDoesNotSendAndResumesTheSameRequestOnce()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new HostDevice(clock) { Logger = new FirstBeginDelayLogger(clock) };
        var native = new NativeCombatBattleHostIo(flow, device);
        var source = new CaptureFrameSource(clock);
        var request = new CombatBattleHostInput(CombatBattleHostInputKind.OpenParty)
        {
            RequestId = Guid.NewGuid(), Source = source.Next(),
            DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency
        };
        var deferred = await native.SendAsync(request, default);
        Assert.Equal(CombatBattleHostInputStatus.NotSent, deferred.Status);
        Assert.Null(deferred.CompletedTimestamp);
        Assert.Equal(0, device.Preparations);
        Assert.Empty(device.Inputs);
        var sent = await native.SendAsync(request with { Source = source.Next() }, default);
        Assert.Equal(CombatBattleHostInputStatus.Sent, sent.Status);
        Assert.NotNull(sent.CompletedTimestamp);
        Assert.Equal(new[] { "party" }, device.Inputs);
        native.ReleaseInput();
        Assert.DoesNotContain("drop", device.Inputs); // 无本次UI已打开的图像证据，不盲关。
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativePartialInputStaysUnknownAndUserCancellationHasPriority(bool cancel)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        using var cancellation = new CancellationTokenSource();
        var device = new HostDevice(clock)
        {
            AfterParty = () =>
            {
                if (cancel) cancellation.Cancel();
                throw new IOException("native partial input");
            }
        };
        var native = new NativeCombatBattleHostIo(flow, device);
        var source = new CaptureFrameSource(clock);
        var request = new CombatBattleHostInput(CombatBattleHostInputKind.OpenParty)
        {
            RequestId = Guid.NewGuid(), Source = source.Next(),
            DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency
        };
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await native.SendAsync(request, cancellation.Token));
        else
        {
            var result = await native.SendAsync(request, default);
            Assert.Equal(CombatBattleHostInputStatus.Unknown, result.Status);
            Assert.IsType<IOException>(result.Error);
            Assert.Null(result.CompletedTimestamp);
        }
        native.ReleaseInput();
        Assert.Equal(new[] { "party" }, device.Inputs);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(100, 40)]
    [InlineData(145, 0)]
    public async Task HostApproachReservesReleaseTimeInsideOriginalBudget(int downDelay, int expectedHold)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new HostDevice(clock) { NativeReceipts = true, DownDelayMs = downDelay };
        var native = new NativeCombatBattleHostIo(flow, device);
        var source = new CaptureFrameSource(clock);
        var result = await native.SendAsync(new(CombatBattleHostInputKind.Approach)
        {
            RequestId = Guid.NewGuid(), Source = source.Next(),
            DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency
        }, default);
        Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(new[] { "forward-down", "forward-up" }, device.Inputs);
        if (expectedHold == 0) Assert.Empty(device.Delays);
        else Assert.Equal(expectedHold, Assert.Single(device.Delays));
        Assert.Equal(2, result.NativeSubmitted);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(200, false)]
    [InlineData(0, true)]
    public async Task HostApproachTimingLogCannotChangeCompletedInput(int logDelay, bool throws)
    {
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), physical);
        var device = new HostDevice(clock) { NativeReceipts = true, DownDelayMs = 100,
            Logger = new HostTimingLogger(() => { clock.Advance(TimeSpan.FromMilliseconds(logDelay)); if (throws) throw new IOException("logger"); }) };
        var native = new NativeCombatBattleHostIo(flow, device);
        var source = new CaptureFrameSource(clock);
        var started = clock.GetTimestamp();
        var result = await native.SendAsync(new(CombatBattleHostInputKind.Approach)
        { RequestId = Guid.NewGuid(), Source = source.Next(), DeadlineTimestamp = started + clock.TimestampFrequency }, default);
        Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
        Assert.Null(result.Error);
        Assert.Equal(140, clock.GetElapsedTime(started, result.CompletedTimestamp!.Value).TotalMilliseconds);
    }

    [Theory]
    [InlineData("slow-up")]
    [InlineData("reject-up")]
    [InlineData("reject-down")]
    [InlineData("cancel")]
    public async Task HostApproachKeepsRealFailuresAndAlwaysAttemptsRelease(string fault)
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), physical);
        var device = new HostDevice(clock) { NativeReceipts = true,
            UpDelayMs = fault == "slow-up" ? 100 : 0,
            RejectForward = down => fault == (down ? "reject-down" : "reject-up"),
            AfterForward = down => { if (down && fault == "cancel") cancellation.Cancel(); } };
        var native = new NativeCombatBattleHostIo(flow, device);
        var source = new CaptureFrameSource(clock);
        var request = new CombatBattleHostInput(CombatBattleHostInputKind.Approach)
        { RequestId = Guid.NewGuid(), Source = source.Next(), DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency };
        if (fault == "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await native.SendAsync(request, cancellation.Token));
        else
        {
            var result = await native.SendAsync(request, default);
            Assert.NotNull(result.Error);
            Assert.Equal(fault == "slow-up" ? CombatBattleHostInputStatus.Sent : CombatBattleHostInputStatus.Unknown, result.Status);
        }
        Assert.Equal(new[] { "forward-down", "forward-up" }, device.Inputs);
    }

    private sealed class HostTimingLogger(Action log) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format)
        { if (format(state, error).StartsWith("HOST_APPROACH_TIMING")) log(); }
    }

    private sealed class HostDevice(FakeTimeProvider clock) : ICombatHostInputDevice
    {
        public bool NativeReceipts { get; init; }
        public bool RejectSecondParty { get; init; }
        private int _partyCalls;
        public InputDispatchCapture? BeginNativeCapture(Action guard) => NativeReceipts ? new(guard) : null;
        public TimeProvider Clock => clock;
        public ILogger Logger { get; init; } = NullLogger.Instance;
        public int Preparations { get; private set; }
        public List<string> Inputs { get; } = [];
        public Action? AfterParty { get; init; }
        public Action? AfterCamera { get; init; }
        public Action<int>? AdvanceClock { get; init; }
        public void PrepareInput() => Preparations++;
        public void MoveCamera(int x, int y) { Inputs.Add("camera"); AfterCamera?.Invoke(); }
        public int DownDelayMs { get; init; }
        public int UpDelayMs { get; init; }
        public Func<bool, bool>? RejectForward { get; init; }
        public Action<bool>? AfterForward { get; init; }
        public List<int> Delays { get; } = [];
        public void MoveForward(bool down)
        {
            Inputs.Add(down ? "forward-down" : "forward-up");
            clock.Advance(TimeSpan.FromMilliseconds(down ? DownDelayMs : UpDelayMs));
            if (NativeReceipts) new WindowsInputMessageDispatcher(null, inputs => RejectForward?.Invoke(down) == true ? 0U : (uint)inputs.Length, () => 5)
                .DispatchInput(new User32.INPUT[1]);
            AfterForward?.Invoke(down);
        }
        public void PressDrop()
        {
            Inputs.Add("drop");
            if (NativeReceipts) new WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0)
                .DispatchInput(new User32.INPUT[2]);
        }
        public void PressParty()
        {
            Inputs.Add("party");
            _partyCalls++;
            if (NativeReceipts) new WindowsInputMessageDispatcher(null,
                inputs => RejectSecondParty && _partyCalls == 2 ? 0U : (uint)inputs.Length, () => 5)
                .DispatchInput(new User32.INPUT[2]);
            if (AdvanceClock != null) AdvanceClock(10);
            else clock.Advance(TimeSpan.FromMilliseconds(10));
            AfterParty?.Invoke();
        }
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Delays.Add(milliseconds);
            if (AdvanceClock != null) AdvanceClock(milliseconds);
            else clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FirstBeginDelayLogger(FakeTimeProvider clock) : ILogger
    {
        private bool _delayed;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (_delayed || !formatter(state, exception).StartsWith("UI_BEGIN")) return;
            _delayed = true;
            clock.Advance(TimeSpan.FromMilliseconds(292));
        }
    }

    [Theory]
    [InlineData("overworld", false, false)]
    [InlineData("overworld", false, true)]
    [InlineData("overworld", true, false)]
    [InlineData("overworld", true, true)]
    [InlineData("domain", false, false)]
    [InlineData("domain", false, true)]
    [InlineData("domain", true, false)]
    [InlineData("domain", true, true)]
    [InlineData("stygian", false, false)]
    [InlineData("stygian", false, true)]
    [InlineData("stygian", true, false)]
    [InlineData("stygian", true, true)]
    public async Task AllSupportedSceneFormatsUseTheSameNativeCastAndCancellationPath(string scene, bool json, bool enhanced)
    {
        var clock = new FakeTimeProvider();
        var action = enhanced ? "e(required,feed=琴),q(required,if=low-hp(琴) && q-ready(琴)),attack(0.1)" : "e,q,attack(0.1)";
        var program = LoadProgram("琴 " + action);
        using var io = new PhysicalReplay(clock, false, 50, program) { ResourceEvents = enhanced };
        io.ScheduleHealth(0, "琴", true);
        if (!enhanced) io.ScheduleEnergy(0, "琴", true);
        using var flow = json ? NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Actions = [new() { Character = "琴", Action = action }]
        }, NativeCombatFlowRunner.CreateAdapter(io), clock: clock)!
            : NativeCombatFlowRunner.Create(CombatScriptParser.ParseContext("琴 " + action).CombatCommands, io, true);
        var sceneIo = new SceneReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(sceneIo, new() { ExternalCompletionAuthority = scene != "overworld", TimeoutSeconds = 120 });
        bool Confirmed() => flow.RuntimeStatistics.RecentEvents.Any(e => e.Actor == "琴" && e.Action == "burst" && e.ReportedResult == nameof(CombatFlowResult.Succeeded));
        for (var i = 0; i < 400 && !Confirmed(); i++) Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.True(Confirmed());
        Assert.Contains(io.Inputs, input => input.Actor == "琴" && input.Skill == Method.Skill);
        Assert.Contains(io.Inputs, input => input.Actor == "琴" && input.Skill == Method.Burst);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var start = clock.GetTimestamp();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.AdvanceAsync(flow, cancelled.Token));
        Assert.InRange(clock.GetElapsedTime(start).TotalMilliseconds, 0, 150);
    }

    private sealed class SceneReplayIo(FakeTimeProvider clock, Guid battleId, CaptureFrameSource? producer = null) : ICombatBattleHostIo
    {
        private readonly CaptureFrameSource _source = producer ?? new(clock);
        public bool NoProgress { get; init; }
        public bool AlignedTarget { get; init; }
        public Func<CombatBattleHostInput, CancellationToken, ValueTask<CombatBattleHostInputResult>>? SendInput { get; init; }
        public int TargetObservations { get; private set; }
        public TimeProvider Clock => clock;
        public Guid BattleId => battleId;
        public CombatBattleObservation ObserveTarget()
        {
            TargetObservations++;
            if (AlignedTarget) return new(_source.Next(), battleId, CombatObservationQuality.Available,
                new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None, new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar),
                1920, 1080) { Control = new(MotionStatus.Unknown, false) };
            return new(_source.Next(), battleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber),
            1920, 1080, NoProgress ? 1UL : (ulong)(clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000))
        { Motion = BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal };
        }
        public PartySetupFinishObservation ObservePartyBar() => throw new InvalidOperationException("有效目标输出阶段不得打开编队");
        public ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct) =>
            SendInput?.Invoke(input, ct) ?? throw new InvalidOperationException("有效目标输出阶段不得抢占输入");
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct) { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); return ValueTask.CompletedTask; }
        public void ReleaseInput() { }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainStrategyStillHonorsConfiguredGuardianCoverageThroughTheNativeKernel(bool json)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(hold)\n那维莱特 e"));
        var guardian = new LegacyGuardianOptions("钟离", true, GuardianCoverageMode.RequireKnownCoverage, 20);
        using var runner = json ? NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Actions = [new() { Character = "那维莱特", Action = "e,attack(0.1)" }]
        }, NativeCombatFlowRunner.CreateAdapter(io), clock: clock, guardian: guardian)!
            : NativeCombatFlowRunner.Create(CombatScriptParser.ParseContext("那维莱特 e,attack(0.1)").CombatCommands,
                io, false, guardian: guardian);
        CombatFlowStep completed = default;
        for (var i = 0; i < 200 && !completed.RoundCompleted; i++) completed = await runner.StepAsync(default);
        Assert.True(completed.Result == CombatFlowResult.Succeeded,
            Newtonsoft.Json.JsonConvert.SerializeObject(new { completed, io.Inputs, runner.RuntimeStatistics.RecentEvents }));
        Assert.Equal("钟离", io.Inputs[0].Actor);
        Assert.Equal("那维莱特", io.Inputs[1].Actor);
        Assert.InRange(io.FirstShieldConfirmed, 0, 3.5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableConfiguredGuardianPreservesBestEffortAndStrictCoveragePolicies(bool strict)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("钟离 e(hold)\n那维莱特 e"));
        io.PrimeSkillCooldown("钟离", 8);
        using var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseContext("那维莱特 e").CombatCommands,
            io, false, guardian: new("钟离", true, strict ? GuardianCoverageMode.RequireKnownCoverage : GuardianCoverageMode.BestEffort, 20));
        var result = await runner.RunRoundAsync(default);
        Assert.Equal(!strict, result == CombatFlowResult.Succeeded);
        Assert.Equal(strict ? 0 : 1, io.Inputs.Count);
    }
    [Fact]
    public async Task CurrentActorExplicitWaitRetainsTheObservedLongCooldownBudget()
    {
        var clock = new FakeTimeProvider();
        var program = CombatFlowProgram.Compile(CombatScriptParser.ParseContext("e(wait,required,record=当前施放)", validate: false));
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("芙宁娜 e"));
        io.SetFrontActor("芙宁娜");
        io.PrimeSkillCooldown("芙宁娜", 20);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var result = await runner.RunRoundAsync(default);
        Assert.Equal(CombatFlowResult.Succeeded, result);
        Assert.NotNull(runner.Context.Find("当前施放"));
        Assert.Equal("芙宁娜", Assert.Single(io.Inputs).Actor);
    }
    private static readonly NativeCombatActor[] RockParty = [new("钟离", 1), new("班尼特", 2), new("娜维娅", 3), new("香菱", 4)];

    [Fact]
    public async Task ANewSatisfiedDemandInsideTheSelectedFeedFragmentCannotSpendTheProducerSkill()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("香菱 q(recharge,from=segment:供能,required,record=有效Q)\nsegment(供能,define) { 班尼特 wait(0.5),e(required,feed=香菱) }");
        using var io = new PhysicalReplay(clock, false, 50, program) { Actors = RockParty };
        io.SetFrontActor("香菱");
        io.ScheduleEnergy(1.5, "香菱", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 200 && runner.Context.Find("有效Q") == null; i++) await runner.StepAsync(default);
        Assert.NotNull(runner.Context.Find("有效Q"));
        Assert.DoesNotContain(io.Inputs, input => input.Actor == "班尼特");
        Assert.Single(io.Inputs);
    }

    [Fact]
    public async Task CrossActorSimultaneousEnergyConditionCannotInventBackgroundEvidence()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("call(供能,if=q-energy-low(香菱) && !q-cd(香菱) && e-ready(班尼特))\nsegment(供能,define) { 班尼特 e(fast,required,feed=香菱) }");
        using var io = new PhysicalReplay(clock, false, 50, program) { Actors = RockParty };
        io.SetFrontActor("娜维娅");
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 100 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task RockStrategyConfirmsDemandThenFeedsAndCompletesTheRecipientsBurst(double cost)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram(LoadPartyScript("00-岩.txt"));
        using var io = new PhysicalReplay(clock, false, cost, program) { Actors = RockParty, RockEnergyEvents = true };
        io.SetFrontActor("娜维娅");
        using var runner = NativeCombatFlowRunner.Create(program, io);
        bool BurstConfirmed() => runner.RuntimeStatistics.RecentEvents.Any(e => e.Actor == "香菱" && e.Action == "burst" &&
            e.ReportedResult == nameof(CombatFlowResult.Succeeded));
        for (var i = 0; i < 800 && !BurstConfirmed(); i++)
            await runner.StepAsync(default);
        var source = Assert.Single(io.Inputs.Where(input => input.Actor == "班尼特" && input.Skill == Method.Skill));
        var burst = Assert.Single(io.Inputs.Where(input => input.Actor == "香菱" && input.Skill == Method.Burst));
        Assert.True(source.At < burst.At);
        Assert.InRange(source.At - io.FirstRockDemandAt, 0, 4);
        Assert.True(BurstConfirmed());
    }

    [Fact]
    public async Task InactivePendingActorDefeatProbeYieldsBeforeSwitchCompletionAndBlocksHostInput()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("那维莱特 e(required)");
        using var io = new PhysicalReplay(clock, false, 50, program) { DropFirstSkill = true };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 60 && io.Inputs.Count == 0; i++) await runner.StepAsync(default);
        Assert.Single(io.Inputs);
        io.SetFrontActor("琴");
        var start = clock.GetTimestamp();
        runner.InspectDefeat(default);
        Assert.InRange(clock.GetElapsedTime(start).TotalMilliseconds, 0, 150);
        Assert.True(runner.HasAwaitingObservation);
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await runner.RunHostOperationAsync(_ => ValueTask.CompletedTask, default));
        for (var i = 0; i < 60 && runner.HasAwaitingObservation; i++) await runner.StepAsync(default);
        Assert.False(runner.HasAwaitingObservation);
        Assert.Single(io.Inputs);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task ExpiredInputIsRecheckedAcrossFramesWithoutSpendingAnotherFailedRound(double cost)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("那维莱特 e(required,record=有效施放)");
        program.AllowHostLoop(true);
        using var io = new PhysicalReplay(clock, false, cost, program) { DropFirstSkill = true };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var failures = 0;
        for (var i = 0; i < 400 && runner.Context.Find("有效施放") == null; i++)
        {
            var step = await runner.StepAsync(default);
            if (step.RoundCompleted && step.Result == CombatFlowResult.Failed) failures++;
        }
        Assert.NotNull(runner.Context.Find("有效施放"));
        Assert.Equal(2, io.Inputs.Count);
        Assert.Equal(1, failures);
        Assert.True(runner.Context.Find("有效施放")!.OccurredAt >= io.Inputs[1].At);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task RequiredOpeningKeepsItsRecoveryProbeUntilBothFreshFramesArrive(double cost)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("strategy(loop=battle)\ncall(开场,once=battle,required,attempts=1,timeout=1)\nsegment(开场,define,record=开场完成) { 那维莱特 e(required) }");
        using var io = new PhysicalReplay(clock, false, cost, program) { DropFirstSkill = true };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 200 && runner.Context.Find("开场完成") == null; i++) await runner.StepAsync(default);
        Assert.NotNull(runner.Context.Find("开场完成"));
        Assert.Equal(2, io.Inputs.Count);
    }

    [Theory]
    [InlineData("那维莱特 e(fast)")]
    [InlineData("琴 q")]
    public async Task PlainOptionalUnreadyActionsRemainSkippedWithoutClaimingACompletedCast(string text)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(text));
        io.PrimeSkillCooldown("那维莱特", 8);
        using var runner = NativeCombatFlowRunner.Create(CombatScriptParser.ParseContext(text).CombatCommands, io, false);
        CombatFlowStep step = default;
        for (var i = 0; i < 100 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Skipped, step.Result);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task PlainSequenceCannotHideAnUnreadyRequiredStepBehindAnEarlierAttack()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("那维莱特 e"));
        io.PrimeSkillCooldown("那维莱特", 8);
        var commands = CombatScriptParser.ParseContext("那维莱特 attack(0.1),e,attack(0.1)").CombatCommands;
        using var runner = NativeCombatFlowRunner.Create(commands, io, false);
        CombatFlowStep step = default;
        for (var i = 0; i < 100 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Equal(CombatFlowResult.Deferred, step.Result);
        Assert.Single(io.Primitives);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task PlainRequiredSequenceAndExplicitPartyTemplateKeepTheirDifferentMissingActorRules()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        var commands = CombatScriptParser.ParseContext("莱依拉 e;琴 e").CombatCommands;
        Assert.Throws<InvalidOperationException>(() => NativeCombatFlowRunner.Create(commands, io, false));
        Assert.Empty(io.Inputs);
        using var runner = NativeCombatFlowRunner.Create(commands, io, false, CombatScriptExecutionMode.LegacyPartyTemplate);
        CombatFlowStep step = default;
        for (var i = 0; i < 100 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Equal("琴", Assert.Single(io.Inputs).Actor);
    }

    [Theory]
    [InlineData(";迪希雅 attack(0.25),e;attack(0.4),s(1)")]
    [InlineData("keypress(f);芙宁娜 e,wait(0.2),e;迪希雅 e;")]
    [InlineData("keypress(f);迪希雅 e;芙宁娜 e;")]
    [InlineData("keypress(f),keypress(f);芙宁娜 e;")]
    [InlineData("attack(0.3),keypress(f),attack(0.3),keypress(f),attack(0.5),keypress(f),s(0.2);迪希雅 e;")]
    [InlineData("keypress(f),wait(0.2),keypress(f),wait(0.2),keypress(f),wait(0.2),keypress(f),wait(0.2),keypress(f);迪希雅 e,wait(0.1),e;芙宁娜 e;")]
    [InlineData(";迪希雅 w(0.01);w(0.01),wait(0.2),w(0.01),wait(0.2),attack(0.4),wait(1),keypress(f),wait(0.2),keypress(f),wait(0.2),keypress(f),wait(0.2),keypress(f),wait(0.2),keypress(f),s(0.2)")]
    public async Task RecordedGatheringFragmentsExecuteWithoutOptionalNamedActors(string text)
    {
        var clock = new FakeTimeProvider();
        var commands = CombatScriptParser.ParseContext(text, false).CombatCommands;
        using var io = new PhysicalReplay(clock, false, 0, LoadProgram("琴 e"))
        { Actors = [new("钟离", 1), new("娜维娅", 2), new("枫原万叶", 3), new("琴", 4)] };
        io.SetFrontActor("钟离");
        using var runner = NativeCombatFlowRunner.Create(commands, io, false,
            CombatScriptExecutionMode.LegacyPartyTemplate, purpose: CombatScriptExecutionPurpose.Pathing);
        CombatFlowStep step = default;
        for (var i = 0; i < 400 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Empty(io.Inputs); // 缺席角色的E从未发送。
        Assert.True(io.Primitives.Count + io.PathingPrimitives.Count > 0);
    }

    [Fact]
    public async Task PlainJsonFiltersMissingPartyAlternativesBeforeSelectingAnAction()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var runner = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Actions = [new() { Character = "莱依拉", Action = "e", Index = 0 },
                new() { Character = "琴", Action = "e", Index = 1 }]
        }, NativeCombatFlowRunner.CreateAdapter(io), clock: clock)!;
        CombatFlowStep step = default;
        for (var i = 0; i < 100 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Equal("琴", Assert.Single(io.Inputs).Actor);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlainHeldInputMacrosKeepOwnershipUntilTheirMatchingRelease(bool json)
    {
        const string actions = "keydown(VK_LBUTTON),wait(0.4),moveby(10,0),keyup(VK_LBUTTON)";
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("那维莱特 e"));
        using var runner = json ? NativeCombatFlowRunner.Create(new JsonCombatStrategy
            { Actions = [new() { Character = "那维莱特", Action = actions }] }, NativeCombatFlowRunner.CreateAdapter(io), clock: clock)!
            : NativeCombatFlowRunner.Create(CombatScriptParser.ParseContext("那维莱特 " + actions).CombatCommands, io, false);
        CombatFlowStep step = default;
        for (var i = 0; i < 100 && !step.RoundCompleted; i++)
        {
            step = await runner.StepAsync(default);
            if (io.HoldingInput)
            {
                Assert.True(runner.IsAtomic);
                await Assert.ThrowsAsync<InvalidOperationException>(async () => await runner.RunHostOperationAsync(_ => ValueTask.CompletedTask, default));
            }
        }
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Equal(new[] { Method.KeyDown, Method.MoveBy, Method.KeyUp }, io.Primitives.Select(command => command.Method));
        Assert.False(io.HoldingInput);
    }

    [Fact]
    public async Task PlainTxtRunsThroughTheNativeContinuationInsteadOfTheLegacySynchronousFallback()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        var script = CombatScriptParser.ParseContext("琴 e");
        using var runner = NativeCombatFlowRunner.Create(script.CombatCommands, io, loop: false);
        Assert.NotNull(runner);
        CombatFlowStep step = default;
        for (var i = 0; i < 100 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Single(io.Inputs);
        Assert.Equal(0, io.SelectionWaitsBeforeFirstSkill);
    }

    [Fact]
    public async Task PlainJsonUsesTheSameNativeSelectionContinuationAndConfirmedSkillInput()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        var strategy = new JsonCombatStrategy { Actions = [new() { Character = "琴", Action = "e" }] };
        Assert.False(JsonCombatFlowExecution.RequiresFlow(strategy));
        using var runner = NativeCombatFlowRunner.Create(strategy, NativeCombatFlowRunner.CreateAdapter(io), clock: clock);
        Assert.NotNull(runner);
        CombatFlowStep step = default;
        for (var i = 0; i < 100 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Single(io.Inputs);
        Assert.Equal("琴", io.Inputs[0].Actor);
        Assert.Equal(0, io.SelectionWaitsBeforeFirstSkill);
    }

    [Theory]
    [InlineData("那维莱特 e(wait,required)")]
    [InlineData("那维莱特 q(recharge,from=琴,required)\nsegment(供能,define) { 琴 e(feed=那维莱特) }")]
    public async Task CancellationDuringCooldownOrRechargeCannotResumeTheRetiredPreparation(string text)
    {
        var program = LoadProgram(text);
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var io = new PhysicalReplay(clock, false, 50, program);
        io.PrimeSkillCooldown("那维莱特", 3);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        Assert.Equal(CombatFlowResult.AwaitingObservation, (await runner.StepAsync(cancellation.Token)).Result);
        io.AfterCapture = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.StepAsync(cancellation.Token));
        Assert.Empty(io.Inputs);
        Assert.False(runner.Context.IsOpen);
        Assert.All(io.CapturedFrames, frame => Assert.True(frame.SrcMat.IsDisposed));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await runner.StepAsync(default));
    }

    [Fact]
    public async Task AlreadyConfirmedSkillWaitsForItsNextCooldownCycleWithoutCreditingThePreviousInput()
    {
        var program = CombatFlowProgram.Compile("timing(短测试CD,cd=2)\n那维莱特 e(required,timing=短测试CD,record=第一次)\n那维莱特 e(wait,required,timing=短测试CD,record=第二次)");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 250 && !step.RoundCompleted; i++)
        {
            step = await runner.StepAsync(default);
            if (runner.Context.Find("第一次") != null && io.Inputs.Count == 1)
                Assert.Null(runner.Context.Find("第二次"));
        }
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Equal(2, io.Inputs.Count);
        Assert.True(io.Inputs[1].At >= io.Inputs[0].At + 2);
        Assert.True(runner.Context.Find("第二次")!.OccurredAt > runner.Context.Find("第一次")!.OccurredAt);
    }

    [Theory]
    [InlineData("那维莱特 e(wait,required,timeout=0.5,record=不应产生)")]
    [InlineData("那维莱特 q(recharge,from=琴,required,timeout=0.5,record=不应产生)\nsegment(供能,define) { 琴 e(feed=那维莱特) }")]
    public async Task WaitingForCooldownOrRechargeCannotResetTheOriginalDeadline(string text)
    {
        var program = LoadProgram(text);
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        io.PrimeSkillCooldown("那维莱特", 3);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 150 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Failed, step.Result);
        Assert.Empty(io.Inputs);
        Assert.Null(runner.Context.Find("不应产生"));
        Assert.False(runner.HasAwaitingObservation);
    }

    [Fact]
    public async Task NewReadyEvidenceDuringProducerSelectionCancelsRechargeAndUsesTheRequestedBurst()
    {
        var program = LoadProgram("那维莱特 q(recharge,from=琴,required,record=直接Q)\nsegment(供能,define) { 琴 e(feed=那维莱特) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        io.ScheduleEnergy(.6, "那维莱特", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 180 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.NotNull(runner.Context.Find("直接Q"));
        var input = Assert.Single(io.Inputs);
        Assert.Equal(("那维莱特", Method.Burst), (input.Actor, input.Skill));
    }

    [Fact]
    public async Task ConditionalPreparationCannotStartANewTimeoutAfterTheCommandDeadlineHasExpired()
    {
        var program = LoadProgram("琴 e(required,if=e-ready(琴),timeout=0.5,record=不应产生)");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 100 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Failed, step.Result);
        Assert.Empty(io.Inputs);
        Assert.Null(runner.Context.Find("不应产生"));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task RechargeKeepsItsDemandWhileTheDeclaredProducerSelectionYieldsAcrossFrames(double observationCost)
    {
        var program = LoadProgram("那维莱特 q(recharge,from=琴,required,timeout=8,record=供能后Q)\nsegment(供能,define) { 琴 e(feed=那维莱特) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, observationCost, program);
        // 外部能量事件明确发生在供能E之后；不模拟未经验证的粒子数量或换算。
        io.ScheduleEnergy(3.1, "那维莱特", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 200 && !step.RoundCompleted; i++)
        {
            var before = runner.Context.Now;
            var preparing = io.Inputs.Count == 0;
            step = await runner.StepAsync(default);
            if (preparing)
            {
                var physicalSeconds = io.Inputs.Count == 0 ? 0 : .05;
                Assert.InRange(runner.Context.Now - before - physicalSeconds, 0, .150001);
            }
        }
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.NotNull(runner.Context.Find("供能后Q"));
        Assert.Equal(new[] { ("琴", Method.Skill), ("那维莱特", Method.Burst) }, io.Inputs.Select(input => (input.Actor, input.Skill)));
        Assert.InRange(io.Inputs[0].At, 0, 4);
        Assert.Equal(0, io.SelectionWaitsBeforeFirstSkill);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task ExplicitSkillCooldownWaitKeepsTheOriginalCommandAndYieldsUntilFreshReadyEvidence(double observationCost)
    {
        var program = LoadProgram("那维莱特 e(wait,required,record=等待后施放)");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, observationCost, program);
        io.PrimeSkillCooldown("那维莱特", 3);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 150 && !step.RoundCompleted; i++)
        {
            var before = runner.Context.Now;
            var inputCount = io.Inputs.Count;
            step = await runner.StepAsync(default);
            // E按键自身的50ms是外部物理持续时间；不能把判定后的固定空等也混在其中。
            var physicalSeconds = io.Inputs.Count == inputCount ? 0 : .05;
            Assert.InRange(runner.Context.Now - before - physicalSeconds, 0, .150001);
            if (runner.Context.Now < 3)
            {
                Assert.Empty(io.Inputs);
                Assert.Null(runner.Context.Find("等待后施放"));
                Assert.False(step.RoundCompleted);
            }
        }
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.NotNull(runner.Context.Find("等待后施放"));
        var input = Assert.Single(io.Inputs);
        Assert.Equal(Method.Skill, input.Skill);
        Assert.InRange(input.At, 3, 4);
        Assert.Empty(io.Primitives);
    }

    [Theory]
    [InlineData("琴 e(required)")]
    [InlineData("琴 e(required,if=e-ready(琴))")]
    public async Task CancelledPreparationCannotResumeFromALateCallbackWithAnotherToken(string command)
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram(command));
        using var runner = NativeCombatFlowRunner.Create(LoadProgram(command), io);
        using var cancellation = new CancellationTokenSource();
        Assert.Equal(CombatFlowResult.AwaitingObservation, (await runner.StepAsync(cancellation.Token)).Result);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.StepAsync(cancellation.Token));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await runner.StepAsync(default));
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task ReceiverSelectionResumesTheFeedWithoutRepeatingTheCompletedSkill()
    {
        var program = LoadProgram("琴 e(required,feed=那维莱特)");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 200 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Single(io.Inputs);
        Assert.Equal("琴", io.Inputs[0].Actor);
        Assert.InRange(runner.Context.Now, 0, 4);
    }

    [Fact]
    public async Task JsonWaitsForItsPreparationThenRechecksTheExistingPriorityPolicy()
    {
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e(required)"));
        var strategy = new JsonCombatStrategy
        {
            Actions = [
                new() { Character = "那维莱特", Index = 0, Action = "attack(0.1,required)", Condition = new() { Expression = "onfield(琴)" } },
                new() { Character = "琴", Index = 1, Action = "e(required)", Condition = new() { Expression = "e-ready(琴)" } }
            ]
        };
        using var runner = NativeCombatFlowRunner.Create(strategy, NativeCombatFlowRunner.CreateAdapter(io), clock: clock)!;
        var waiting = false;
        for (var i = 0; i < 150 && io.Inputs.Count == 0 && io.Primitives.Count == 0; i++)
        {
            var step = await runner.StepAsync(default);
            if (step.Result == CombatFlowResult.AwaitingObservation)
            {
                waiting = true;
                Assert.False(runner.IsAtRootBoundary);
            }
        }
        Assert.True(waiting);
        Assert.Empty(io.Inputs);
        Assert.Equal(Method.Attack, Assert.Single(io.Primitives).Method);
    }

    [Fact]
    public async Task RequiredBurstPreparationDoesNotUseInlineSelectionWaitsOrLoseItsOriginalCommand()
    {
        var program = LoadProgram("琴 q(required)");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        // 后台最初没有就绪证据，选角过程中发生明确的满能事件。
        io.ScheduleEnergy(1.05, "琴", true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 100 && io.Inputs.Count == 0; i++) await runner.StepAsync(default);
        var input = Assert.Single(io.Inputs);
        Assert.Equal(Method.Burst, input.Skill);
        Assert.Equal(0, io.SelectionWaitsBeforeFirstSkill);
        Assert.InRange(input.At, 0, 4);
    }

    [Theory]
    [InlineData("琴 e(required)")]
    [InlineData("琴 e(required,if=e-ready(琴))")]
    public async Task SelectingAnotherActorYieldsAcrossStepsWithoutSpendingItsPhysicalWaitInsideOneDecision(string command)
    {
        var program = LoadProgram(command);
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 80 && io.Inputs.Count == 0; i++)
        {
            var before = runner.Context.Now;
            await runner.StepAsync(default);
            Assert.InRange(runner.Context.Now - before, 0, .150001);
        }
        Assert.Single(io.Inputs);
        Assert.Equal("琴", io.Inputs[0].Actor);
    }

    [Theory]
    [InlineData(300)]
    [InlineData(1800)]
    public async Task BlockingTailObservationReleasesInputAndCannotCompleteTheMacro(double blockedCost)
    {
        var program = LoadProgram("call(宏,required)\nsegment(宏,define,atomic,record=完成) { 那维莱特 keydown(VK_LBUTTON), wait(0.08), moveby(10,0), keyup(VK_LBUTTON) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program) { HeldCaptureCost = blockedCost };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 20 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Null(runner.Context.Find("完成"));
        Assert.Equal(CombatFlowResult.Failed, step.Result);
        Assert.Single(io.Primitives);
        Assert.False(io.HoldingInput);
        Assert.Contains(runner.RuntimeStatistics.RecentEvents, entry => entry.Reason?.Contains("150ms") == true);
        runner.Dispose();
        Assert.All(io.CapturedFrames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task CancellationInsideTheTailObservationReleasesHeldInputBeforeReturning()
    {
        var program = LoadProgram("segment(宏,atomic) { 那维莱特 keydown(VK_LBUTTON), wait(0.08), keyup(VK_LBUTTON) }");
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var io = new PhysicalReplay(clock, false, 50, program);
        io.AfterCapture = () => { if (io.Primitives.Count != 0) cancellation.Cancel(); };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 20 && !io.HoldingInput; i++) await runner.StepAsync(cancellation.Token);
        Assert.True(io.HoldingInput);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.StepAsync(cancellation.Token));
        Assert.False(io.HoldingInput);
        Assert.Single(io.Primitives);
    }

    [Theory]
    [InlineData(false, 50)]
    [InlineData(false, 150)]
    [InlineData(true, 50)]
    [InlineData(true, 150)]
    public async Task WaterBranchWithoutMaintenanceMeetsItsOriginalWindow(bool burstReady, double decisionCost)
    {
        var script = LoadWaterScript();
        // 沿用发布策略原定义体；此独立场景只让所选主轴在新护盾后立即开始，明确排除必要维护压力。
        var program = LoadProgram("timing(离线护盾,cd=12,duration=20)\ncall(开场,once=battle,required)\n" +
            "branch(if=q-ready(那维莱特),then=Q双喷,else=E单喷,unknown=E单喷)\n" + script[script.IndexOf("segment(开场", StringComparison.Ordinal)..]);
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, burstReady, decisionCost, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        var ledger = new OutputLedger();
        CombatFlowStep step = default;
        for (var i = 0; i < 500 && !step.RoundCompleted; i++)
        {
            step = await runner.StepAsync(default);
            ledger.Observe(runner.RuntimeStatistics, runner.Context.Find("喷射宏完成")?.Generation,
                runner.Context.Find("喷射宏完成")?.OccurredAt, io.HeldReleases);
        }
        var branch = Assert.Single(ledger.Sources).Value;
        var macros = ledger.Macros.Where(macro => macro.Branch == branch.Branch.Id).ToArray();
        Assert.Equal(burstReady ? 2 : 1, macros.Length);
        Assert.Empty(ledger.Maintenance);
        Assert.Equal((burstReady ? 2 : 1) * 26, io.Primitives.Count);
        var total = macros[^1].CompletedAt - branch.Branch.EnteredAt;
        output.WriteLine($"BRANCH_NO_MAINTENANCE burst={burstReady} decision={decisionCost} total={total:F3}");
        Assert.InRange(total, 0, burstReady ? 16 : 10);
        if (!burstReady) Assert.InRange(macros[0].StartedAt - branch.Branch.EnteredAt, 0, 5);
    }

    [Theory]
    [InlineData(50, 4.0)]
    [InlineData(150, 6.0)]
    public async Task RawAtomicSweepOverlapsObservationWithExistingWaits(double cost, double maximum)
    {
        var script = "segment(喷射宏,atomic,timeout=16,record=宏完成) {\n那维莱特 keydown(VK_LBUTTON), wait(1.6), " +
            string.Join(", ", Enumerable.Repeat("moveby(1800,0), wait(0.08)", 24)) + ", keyup(VK_LBUTTON)\n}";
        var program = LoadProgram(script);
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, cost, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 150 && runner.Context.Find("宏完成") == null; i++) await runner.StepAsync(default);
        Assert.NotNull(runner.Context.Find("宏完成"));
        Assert.Equal(26, io.Primitives.Count);
        Assert.InRange(runner.Context.Now, 3.52, maximum);
        Assert.Equal(Method.KeyDown, io.Primitives[0].Method);
        Assert.Equal(Method.KeyUp, io.Primitives[^1].Method);
        Assert.All(io.Primitives.Skip(1).SkipLast(1), command => Assert.Equal(Method.MoveBy, command.Method));
    }

    [Fact]
    public async Task SharedNativeAdapterCarriesThePreparedCooldownFrameIntoAdmission()
    {
        var program = LoadProgram("琴 e(required,if=e-ready(琴))");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 100 && runner.Context.Now < 4 && io.Inputs.Count == 0; i++) await runner.StepAsync(default);
        Assert.Single(io.Inputs);
        Assert.Single(io.FirstInputCooldownSources.Distinct());
    }

    [Fact]
    public async Task SharedNativeAdapterConfirmsOneSkillWithoutRepeatingItsPhysicalInput()
    {
        var program = LoadProgram("琴 e(required)");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 40 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.True(step.RoundCompleted);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Single(io.Inputs);
        Assert.Equal("琴", io.Inputs[0].Actor);
        Assert.InRange(runner.Context.Now, 1, 4);
    }

    [Fact]
    public async Task SharedNativeAdapterRejectsInputWhenCancellationArrivesDuringCapture()
    {
        var program = LoadProgram("琴 e(required)");
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var io = new PhysicalReplay(clock, false, 50, program) { AfterCapture = cancellation.Cancel };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await runner.StepAsync(cancellation.Token));
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task SharedNativeAdapterObservesDeclaredEnergyAndHealingEvents(double cost)
    {
        var program = LoadProgram("琴 e(required,feed=琴)\n琴 q(required,if=low-hp(琴) && q-ready(琴))");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, cost, program) { ResourceEvents = true };
        io.ScheduleHealth(0, "琴", low: true);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        // 快步不再固定多睡50ms；按原物理场景时限观察，而非固定旧调用次数。
        for (var i = 0; i < 200 && runner.Context.Now < 8 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Equal(CombatFlowResult.Succeeded, step.Result);
        Assert.Equal(new[] { Method.Skill, Method.Burst }, io.Inputs.Select(input => input.Skill));
        Assert.All(io.Inputs, input => Assert.InRange(input.At, 0, 4));
        using var after = io.Capture();
        Assert.False(io.ReadLowHp(after!));
    }

    [Theory]
    [InlineData("frozen")]
    [InlineData("future")]
    [InlineData("restart")]
    [InlineData("foreign-actor")]
    public async Task AtomicEvidenceCannotCrossSourceOrActorBoundaries(string fault)
    {
        var program = LoadProgram("call(宏,required)\nsegment(宏,define,atomic,record=完成) { 那维莱特 keydown(VK_LBUTTON), wait(1.6), moveby(10,0), keyup(VK_LBUTTON) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, 50, program) { HeldFrameFault = fault };
        using var runner = NativeCombatFlowRunner.Create(program, io);
        CombatFlowStep step = default;
        for (var i = 0; i < 20 && !step.RoundCompleted; i++) step = await runner.StepAsync(default);
        Assert.Equal(CombatFlowResult.Failed, step.Result);
        Assert.Null(runner.Context.Find("完成"));
        Assert.Single(io.Primitives);
        Assert.False(io.HoldingInput);
        Assert.Empty(io.Selections);
    }

    [Theory]
    [InlineData(50)]
    [InlineData(150)]
    public async Task KnownCooldownUsesTheDeclaredFallbackWithinTwoSeconds(double cost)
    {
        var program = LoadProgram("call(主轴,required)\nsegment(主轴,define,onfail=保底) { 那维莱特 e(fast,required) }\nsegment(保底,define) { 那维莱特 attack(0.5) }");
        var clock = new FakeTimeProvider();
        using var io = new PhysicalReplay(clock, false, cost, program);
        io.PrimeKnownSkillCooldown("那维莱特", 8);
        using var runner = NativeCombatFlowRunner.Create(program, io);
        for (var i = 0; i < 15 && double.IsPositiveInfinity(io.FirstAttack); i++) await runner.StepAsync(default);
        Assert.InRange(io.FirstAttack, 0, 2);
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(false, 150, false)]
    [InlineData(true, 150, false)]
    [InlineData(false, 50, false)]
    [InlineData(true, 50, false)]
    [InlineData(false, 150, true)]
    [InlineData(true, 150, true)]
    [InlineData(false, 50, true)]
    [InlineData(true, 50, true)]
    public async Task WaterRotationKeepsUsefulOutputWithBoundedNativeDecisionCosts(bool burstReady, double decisionCost, bool controlInterruption)
    {
        var script = LoadWaterScript();
        var program = LoadProgram(script);
        var clock = new FakeTimeProvider();
        using var game = new PhysicalReplay(clock, burstReady, decisionCost, program)
        { RecoveryPulsesRequired = controlInterruption ? 3 : 0, ControlStartsAt = 20 };
        using var execution = NativeCombatFlowRunner.Create(program, game);
        var ledger = new OutputLedger();
        for (var i = 0; i < 1600 && execution.Context.Now < 60; i++)
        {
            var step = await execution.StepAsync(default);
            ledger.Observe(execution.RuntimeStatistics, execution.Context.Find("喷射宏完成")?.Generation,
                execution.Context.Find("喷射宏完成")?.OccurredAt, game.HeldReleases);
            if (i < 5 || step.RoundCompleted || step.Result != CombatFlowResult.Succeeded)
                output.WriteLine($"step={i} t={execution.Context.Now:F3} result={step.Result} round={step.RoundCompleted} atomic={execution.IsAtomic} shield={execution.Context.Remaining("护盾")} last={execution.RuntimeStatistics.RecentEvents.LastOrDefault()}");
        }
        output.WriteLine($"burst={burstReady} cost={decisionCost} time={execution.Context.Now:F3}, inputs={string.Join(",", game.Inputs)}, macros={string.Join(",", ledger.Macros)}, outputs={string.Join(",", ledger.Outputs)}");
        Assert.NotEmpty(ledger.Macros);
        Assert.Equal(controlInterruption ? 3 : 0, game.ControlPulses);
        Assert.All(Enumerable.Range(0, 4), interval => Assert.Contains(ledger.Outputs,
            at => at >= interval * 15 && at < (interval + 1) * 15));
        Assert.True(game.FirstShieldConfirmed <= 3.5, $"opening={game.FirstShieldConfirmed:F3}");
        var branch = ledger.Sources.Values.First(source => source.Branch.Name == (burstReady ? "Q双喷" : "E单喷"));
        var macros = ledger.Macros.Where(macro => macro.Branch == branch.Branch.Id).ToArray();
        Assert.True(macros.Length >= (burstReady ? 2 : 1), "不能把后续E fallback的宏算作同一次Q双喷");
        var maintenance = ledger.Maintenance.Values.Where(item => item.Branch == branch.Branch.Id).ToArray();
        Assert.InRange(maintenance.Length, 0, 1);
        var maintenanceSeconds = maintenance.Sum(item => macros.First(macro => macro.StartedAt >= item.Call.EnteredAt).StartedAt - item.Call.EnteredAt);
        Assert.InRange(maintenanceSeconds, 0, 6);
        var total = macros[burstReady ? 1 : 0].CompletedAt - branch.Branch.EnteredAt;
        output.WriteLine($"BRANCH_RESULT burst={burstReady} decision={decisionCost} total={total:F3} maintenance={maintenanceSeconds:F3} count={maintenance.Length}");
        if (!burstReady) Assert.True(macros[0].StartedAt - branch.Branch.EnteredAt <= 5 + (maintenance.Length == 0 ? 0 : 6));
        Assert.True(total <= (burstReady ? 16 : 10) + (maintenance.Length == 0 ? 0 : 6), "原分支未在1.5规定的总窗口内完成；维护仍计入原始总钟");
    }

    private static string LoadWaterScript() => LoadPartyScript("00-水.txt");

    private static string LoadPartyScript(string file)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        return File.ReadAllText(Path.Combine(root.FullName, "BetterGenshinImpact", "User", "AutoFight", file));
    }

    private static CombatFlowProgram LoadProgram(string script)
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "BetterGenshinImpact.sln"))) root = root.Parent;
        Assert.NotNull(root);
        var data = Path.Combine(root.FullName, "BetterGenshinImpact", "GameTask", "AutoFight", "Assets", "SkillData");
        var seed = CombatSkillCatalog.ParseLocalJson<SkillCatalogSeed>(File.ReadAllText(Path.Combine(data, "builtin-skills.json")));
        var rules = CombatSkillCatalog.ParseLocalJson<SkillMechanicsRulePack>(File.ReadAllText(Path.Combine(data, "builtin-mechanics.json")));
        return CombatFlowProgram.Compile(script, new SkillCatalogSnapshot(rules.Apply(seed.Skills.ToDictionary(skill => skill.Id))));
    }

    private sealed class OutputLedger
    {
        internal sealed record Source(Guid Attempt, CombatCallContext Branch);
        internal sealed record Macro(Guid Id, Guid Branch, double StartedAt, double CompletedAt);
        private long _sequence;
        private long? _completionGeneration;
        private readonly Dictionary<Guid, (Guid Branch, double At)> _starts = new();
        private Macro? _released;
        internal Dictionary<Guid, Source> Sources { get; } = new();
        internal Dictionary<Guid, (Guid Branch, CombatCallContext Call)> Maintenance { get; } = new();
        internal List<Macro> Macros { get; } = [];
        internal List<double> Outputs { get; } = [];
        internal void Observe(CombatFlowStatistics statistics, long? generation, double? completedAt, HashSet<double> heldReleases)
        {
            foreach (var entry in statistics.RecentEvents.Where(entry => entry.Sequence > _sequence))
            {
                _sequence = entry.Sequence;
                var branch = entry.CallPath.LastOrDefault(call => call.Name is "E单喷" or "Q双喷");
                if (branch != null && entry.CallPath.LastOrDefault(call => call.Name == "补盾") is { } maintenance)
                    Maintenance.TryAdd(maintenance.Id, (branch.Id, maintenance));
                if (branch != null && entry.Actor == "那维莱特" && entry.Action is "skill" or "burst" &&
                    entry.InputStarted && entry.AttemptId is { } attempt)
                    Sources.TryAdd(branch.Id, new(attempt, branch));
                if (entry.ReportedResult != nameof(CombatFlowResult.Succeeded)) continue;
                if (entry.Action == "attack" || entry.Actor == "那维莱特" && entry.Action is "skill" or "burst")
                    Outputs.Add(entry.At);
                var macro = entry.CallPath.LastOrDefault(call => call.Name == "喷射宏");
                if (macro == null || branch == null) continue;
                if (entry.Action == "keydown" && entry.InputAt is { } down) _starts[macro.Id] = (branch.Id, down);
                if (entry.Action == "keyup" && entry.InputAt is { } up && heldReleases.Contains(up) && _starts.TryGetValue(macro.Id, out var started))
                    _released = new(macro.Id, started.Branch, started.At, entry.At);
            }
            if (generation != null && generation != _completionGeneration && completedAt != null && _released != null)
            {
                if (_released.CompletedAt <= completedAt && !Macros.Any(macro => macro.Id == _released.Id))
                {
                    Macros.Add(_released with { CompletedAt = completedAt.Value });
                    Outputs.Add(completedAt.Value);
                }
            }
            _completionGeneration = generation;
        }
    }

    private sealed class PhysicalReplay : INativeCombatIo, IDisposable
    {
        private readonly FakeTimeProvider _clock;
        private readonly CaptureFrameSource _producer;
        private readonly double _cost;
        private readonly bool _burstReady;
        private readonly CombatFlowProgram _program;
        private readonly long _started;
        private long _nextFrameTimestamp;
        private CaptureFrameStamp _latest;
        private string _actor = "那维莱特";
        private string? _selected;
        private double _selectionCompletes;
        private bool _held;
        private readonly HashSet<User32.VK> _heldKeys = [];
        public List<(User32.VK Key, bool Up, double At, bool ReleaseAll)> KeyEvents { get; } = [];
        private readonly Dictionary<(string, Method), (double Visible, double Ready)> _cooldowns = new();
        private readonly Dictionary<string, double> _knownECdUntil = new();
        private readonly Dictionary<CaptureFrameStamp, Snapshot> _snapshots = new();
        private readonly List<CaptureFrameStamp> _cooldownReadSources = [];
        private readonly List<(double At, string Actor, bool Full)> _energyEvents = [];
        private readonly List<(double At, string Actor, bool Low)> _healthEvents = [];
        public List<(string Actor, Method Skill, double At)> Inputs { get; } = [];
        public IReadOnlyList<CaptureFrameStamp> FirstInputCooldownSources { get; private set; } = [];
        public HashSet<double> HeldReleases { get; } = [];
        public List<CombatCommand> Primitives { get; } = [];
        public List<CombatCommand> PathingPrimitives { get; } = [];
        public string? PathingJumpFault { get; init; }
        public Action? BeforePathingInput { get; init; }
        public int PathingJumpAttempts { get; private set; }
        public List<CaptureFrameStamp> PathingJumpSources { get; } = [];
        private bool _restartedDuringWait;
        public string? MoveByReceiptFault { get; init; }
        public List<bool> MoveByHeldStates { get; } = [];
        public bool CombatHudVisible { get; init; } = true;
        public double HeldActiveReadCost { get; init; }
        public double HeldControlReadCost { get; init; }
        public double ControlReadCost { get; init; }
        public Exception? PrimitivePostError { get; init; }
        public double FirstShieldConfirmed { get; private set; } = double.PositiveInfinity;
        public Action? AfterCapture { get; set; }
        public Action? AfterRelease { get; set; }
        public double IgnoreSwitchUntil { get; init; }
        public bool UnknownSwitchOutcome { get; init; }
        public bool SelectionNeedsMovement { get; init; }
        public bool FailApproach { get; init; }
        public int ApproachPulses { get; private set; }
        public CaptureFrameSource Producer => _producer;
        public bool HideSideBurstDuringSelection { get; init; }
        public string? InitialFrameFault { get; init; }
        public double InitialFrameFaultUntil { get; set; } = double.PositiveInfinity;
        public double? HeldCaptureCost { get; init; }
        public string? HeldFrameFault { get; init; }
        private CaptureFrameStamp _lastDelivered;
        public List<int> Selections { get; } = [];
        public List<(CaptureFrameStamp Source, int Index)> ActiveObservations { get; } = [];
        public long LastSelectionTimestamp { get; private set; }
        public double FirstAttack { get; private set; } = double.PositiveInfinity;
        public void PrimeKnownSkillCooldown(string actor, double seconds) => _knownECdUntil[actor] = Now + seconds;
        public void PrimeSkillCooldown(string actor, double seconds)
        {
            _cooldowns[(actor, Method.Skill)] = (Now, Now + seconds);
            PrimeKnownSkillCooldown(actor, seconds);
        }
        public bool HoldingInput => _held;
        public bool FreezeWhileHeld { get; init; }
        public bool NonBlockingHeldCapture { get; init; }
        public bool HideHudAfterInteraction { get; init; }
        public bool KnownCannonInteraction { get; init; }
        public bool ForeignCannonSource { get; init; }
        public List<double> CannonObservationTimes { get; } = [];
        public CannonUiObservation ReadCannonScene(ImageRegion frame)
        {
            CannonObservationTimes.Add(Now);
            return KnownCannonInteraction && _interactionUi
                ? new(ForeignCannonSource ? new CaptureFrameSource(_clock).Next() : frame.FrameStamp,
                    4, true, "神居岛崩炮", "Enter发射") : default;
        }
        public bool RepeatInteractionFrame { get; init; }
        public double SkillUnreadyUntil { get; init; }
        public double BurstUnknownUntil { get; init; }
        public double DelayLatenessMs { get; init; }
        private bool _interactionUi;
        public int RecoveryPulsesRequired { get; init; }
        public double ControlStartsAt { get; init; }
        public int ControlPulses { get; private set; }
        public bool FailControlPulse { get; init; }
        public double ControlPrepareDelay { get; init; }
        private ICombatHostInputDevice? _controlDevice;
        public ICombatHostInputDevice ControlDevice => _controlDevice ??= new ReplayControlDevice(this);
        public Func<double, bool>? ControlAt { get; init; }
        public MotionStatus ObservedMotion { get; init; } = MotionStatus.Unknown;
        public Func<double, CombatControlObservation>? ControlOverride { get; init; }
        public BetterGenshinImpact.GameTask.Common.BgiVision.CombatControlObservation ReadControl(ImageRegion frame)
        {
            var result = ControlOverride?.Invoke(Frame(frame).At) ?? new(ObservedMotion, Frame(frame).Controlled);
            Advance(ControlReadCost + (_held ? HeldControlReadCost : 0));
            return result;
        }
        public List<ImageRegion> CapturedFrames { get; } = [];
        public bool ResourceEvents { get; init; }
        public bool DropFirstSkill { get; init; }
        public bool DropAllSkills { get; init; }
        public bool CompleteSkillReceipts { get; init; }
        public List<Guid> SkillRequests { get; } = [];
        public bool RockEnergyEvents { get; init; }
        public double FirstRockDemandAt { get; private set; } = double.PositiveInfinity;
        public void SetFrontActor(string actor) { _actor = actor; _selected = null; }
        public void ScheduleHealth(double at, string actor, bool low) => _healthEvents.Add((at, actor, low));
        public void ScheduleEnergy(double at, string actor, bool full) => _energyEvents.Add((at, actor, full));
        public int SelectionWaitsBeforeFirstSkill { get; private set; }
        private double Now => _clock.GetElapsedTime(_started).TotalSeconds;
        private sealed record Snapshot(string? Actor, double At, bool Controlled);

        public TimeProvider Clock => _clock;
        public double DpiScale { get; init; } = 1;
        public Microsoft.Extensions.Logging.ILogger Logger { get; init; } = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public CombatInputCoordinator InputCoordinator { get; } = new();
        public IReadOnlyList<NativeCombatActor> Actors { get; init; } = new[]
        { new NativeCombatActor("钟离", 1), new("芙宁娜", 2), new("那维莱特", 3), new("琴", 4) };
        public double LastFinishCheckAge => 0;

        public PhysicalReplay(FakeTimeProvider clock, bool burstReady, double cost, CombatFlowProgram program)
        {
            Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
            _clock = clock; _producer = new(clock); _cost = cost; _burstReady = burstReady; _program = program;
            _started = clock.GetTimestamp(); _latest = _producer.Next();
            _nextFrameTimestamp = _started + clock.TimestampFrequency / 20;
        }

        private void Advance(double milliseconds)
        {
            var target = _clock.GetTimestamp() + (long)Math.Round(milliseconds * _clock.TimestampFrequency / 1000);
            while (_clock.GetTimestamp() < target)
            {
                var next = Math.Min(target, _nextFrameTimestamp);
                _clock.Advance(_clock.GetElapsedTime(_clock.GetTimestamp(), next));
                if (_selected != null && Now >= _selectionCompletes) { _actor = _selected; _selected = null; }
                if (_clock.GetTimestamp() == _nextFrameTimestamp)
                {
                    _latest = _producer.Next();
                    _nextFrameTimestamp += _clock.TimestampFrequency / 20;
                }
            }
        }

        public ImageRegion? Capture()
        {
            var wait = _held && NonBlockingHeldCapture ? 0 : _clock.GetElapsedTime(_clock.GetTimestamp(), _nextFrameTimestamp).TotalMilliseconds;
            Advance(wait);
            var source = _latest;
            if (PathingJumpSources.Count > 0 && PathingJumpFault == "repeat") source = PathingJumpSources[0];
            var initialFault = Now < InitialFrameFaultUntil ? InitialFrameFault : null;
            if (initialFault == "stale") source = source with { CapturedTimestamp = source.CapturedTimestamp - _clock.TimestampFrequency / 5 };
            if (initialFault == "repeated" && _lastDelivered.IsKnown) source = _lastDelivered;
            if (initialFault == "cross-session") { _producer.Restart(); source = _producer.Next(); }
            if (_interactionUi && RepeatInteractionFrame) source = _lastDelivered;
            if (_held && HeldFrameFault == "frozen") source = _lastDelivered;
            if (_held && HeldFrameFault == "future") source = source with { CapturedTimestamp = _clock.GetTimestamp() + _clock.TimestampFrequency };
            if (_held && HeldFrameFault == "restart") { _producer.Restart(); source = _producer.Next(); }
            var at = _clock.GetElapsedTime(_started, source.CapturedTimestamp).TotalSeconds;
            var animation = _cooldowns.TryGetValue((_actor, Method.Burst), out var burst) && at < burst.Visible;
            _snapshots[source] = new(animation || _interactionUi || _held && HeldFrameFault == "no-hud" ? null : _held && HeldFrameFault == "foreign-actor" ? "琴" : _actor, at,
                FreezeWhileHeld && _held || ControlAt?.Invoke(at) == true || at >= ControlStartsAt && ControlPulses < RecoveryPulsesRequired);
            _lastDelivered = source;
            Advance(Math.Max(0, (initialFault == "slow" ? 250 : _held ? HeldCaptureCost ?? _cost : _cost) - wait));
            AfterCapture?.Invoke();
            if (initialFault == "null") return null;
            var frame = new ImageRegion(new Mat(1, 1, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source };
            CapturedFrames.Add(frame);
            return frame;
        }

        private Snapshot Frame(ImageRegion frame)
        {
            ObjectDisposedException.ThrowIf(frame.SrcMat.IsDisposed, frame);
            return _snapshots[frame.FrameStamp];
        }
        private sealed class ReplayControlDevice(PhysicalReplay owner) : ICombatHostInputDevice
        {
            public TimeProvider Clock => owner.Clock;
            public ILogger Logger => owner.Logger;
            public void PrepareInput() => owner.Advance(owner.ControlPrepareDelay);
            public void MoveCamera(int x, int y) => throw new InvalidOperationException("控制恢复不能转镜");
            public void MoveForward(bool down)
            {
                if (!owner.SelectionNeedsMovement) throw new InvalidOperationException("当前外部场景不允许移动");
                owner._held = down;
                if (down) owner.ApproachPulses++;
                if (down && owner.FailApproach) throw new IOException("native movement outcome unknown");
            }
            public void PressDrop() => throw new InvalidOperationException("控制恢复不能退出/脱离");
            public void PressParty() => throw new InvalidOperationException("控制恢复不能打开编队");
            public void PressBreakout()
            {
                owner.ControlPulses++; owner.Advance(10);
                if (owner.FailControlPulse) throw new IOException("partial control input");
            }
            public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => new(owner.DelayAsync(milliseconds, ct));
        }
        public bool IsCombatHud(ImageRegion frame) => CombatHudVisible && Frame(frame).Actor != null;
        public bool IsMainUi(ImageRegion frame) => IsCombatHud(frame);
        public int ReadActive(ImageRegion frame, AvatarActiveCheckContext context)
        {
            var index = Actors.FirstOrDefault(actor => actor.Name == Frame(frame).Actor)?.Index ?? -1;
            ActiveObservations.Add((frame.FrameStamp, index));
            if (_held) Advance(HeldActiveReadCost);
            return index;
        }
        public bool? IsActorActive(NativeCombatActor actor, ImageRegion frame) =>
            Frame(frame).Actor is { } active ? active == actor.Name : null;
        private bool Cooling(NativeCombatActor actor, Method skill, double at) =>
            _cooldowns.TryGetValue((actor.Name, skill), out var cd) && at >= cd.Visible && at < cd.Ready;
        private bool EnergyFull(NativeCombatActor actor, double at)
        {
            var events = _energyEvents.Where(item => item.Actor == actor.Name && item.At <= at).OrderBy(item => item.At).ToArray();
            return events.Length != 0 ? events[^1].Full : _burstReady && actor.Name == "那维莱特";
        }
        public double ReadSkillCooldown(NativeCombatActor actor, ImageRegion frame)
        {
            _cooldownReadSources.Add(frame.FrameStamp);
            var at = Frame(frame).At;
            return Cooling(actor, Method.Skill, at) ? _cooldowns[(actor.Name, Method.Skill)].Ready - at : 0;
        }
        public bool IsSkillReady(NativeCombatActor actor, ImageRegion frame, double cooldown)
        {
            if (cooldown > 0) _knownECdUntil[actor.Name] = Now + cooldown;
            return IsActorActive(actor, frame) == true && cooldown <= 0 && Frame(frame).At >= SkillUnreadyUntil;
        }
        public BurstObservation ReadBurst(ImageRegion frame, bool active)
        {
            var sample = Frame(frame);
            if (sample.At < BurstUnknownUntil) return default;
            var actor = Actors.FirstOrDefault(item => item.Name == sample.Actor);
            if (actor?.Name == "香菱" && !EnergyFull(actor, sample.At) && !Cooling(actor, Method.Burst, sample.At))
                FirstRockDemandAt = Math.Min(FirstRockDemandAt, sample.At);
            return active && actor != null ? new(EnergyFull(actor, sample.At), Cooling(actor, Method.Burst, sample.At)) : default;
        }
        public bool ReadLowHp(ImageRegion frame)
        {
            var sample = Frame(frame);
            var events = _healthEvents.Where(item => item.Actor == sample.Actor && item.At <= sample.At).OrderBy(item => item.At).ToArray();
            return events.Length != 0 && events[^1].Low;
        }
        public HashSet<int> ReadSideBurstReady(ImageRegion frame)
        {
            if (HideSideBurstDuringSelection && _selected != null) return [];
            var sample = Frame(frame);
            return Actors.Where(actor => EnergyFull(actor, sample.At) && !Cooling(actor, Method.Burst, sample.At))
                .Select(actor => actor.Index).ToHashSet();
        }
        public bool TryGetKnownSkillCooldown(string actor, out double cooldown)
        {
            cooldown = Math.Max(0, _knownECdUntil.GetValueOrDefault(actor) - Now);
            return _knownECdUntil.ContainsKey(actor);
        }
        public void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc)
        {
            _knownECdUntil[actor.Name] = Now + cooldown;
            if (actor.Name == "钟离") FirstShieldConfirmed = Math.Min(FirstShieldConfirmed, Now);
        }
        public Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken ct)
        {
            if (TryGetKnownSkillCooldown(actor.Name, out var cooldown) && cooldown > 0) WaitForSelection((int)Math.Ceiling(cooldown * 1000), ct);
            return Task.CompletedTask;
        }
        public Action? BeforeSelectionInput { get; set; }
        public Exception? InputReleaseError { get; set; }
        public CombatBattleHostInputResult SelectActor(int index, CombatNativeInputRequest request, CancellationToken ct)
        {
            BeforeSelectionInput?.Invoke();
            // 与生产NativeCombatIo相同：选角物理输入也受当前动作维护/预算约束。
            CombatActionScope.Current?.Check();
            LastSelectionTimestamp = Clock.GetTimestamp();
            Selections.Add(index);
            ct.ThrowIfCancellationRequested();
            var actor = Actors.Single(item => item.Index == index).Name;
            // OS已接收不代表游戏能立刻换人：此窗口内游戏明确忽略按键。
            if (Now >= IgnoreSwitchUntil && (!SelectionNeedsMovement || ApproachPulses > 0) && _selected == null && _actor != actor)
            { _selected = actor; _selectionCompletes = Now + 1; }
            return UnknownSwitchOutcome
                ? new(CombatBattleHostInputStatus.Unknown)
                    { ObservableAfterTimestamp = Clock.GetTimestamp(), NativeRequested = 2, NativeSubmitted = 1 }
                : new(CombatBattleHostInputStatus.Sent, Clock.GetTimestamp());
        }
        public bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken ct) => false;
        public void WaitForSelection(int milliseconds, CancellationToken ct)
        {
            if (Inputs.Count == 0) SelectionWaitsBeforeFirstSkill++;
            ct.ThrowIfCancellationRequested();
            if (CombatActionScope.Current is { } scope) scope.WaitAsync(milliseconds, DelayAsync).GetAwaiter().GetResult();
            else Advance(milliseconds);
            ct.ThrowIfCancellationRequested();
        }
        public void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<ImageRegion> selection, CancellationToken ct) =>
            throw new InvalidOperationException("健康回放意外进入非战斗恢复");
        public void CheckDefeated(ImageRegion frame, CancellationToken ct) { ct.ThrowIfCancellationRequested(); }
        private void Send(NativeCombatActor actor, Method skill, bool hold)
        {
            if (Inputs.Count == 0) FirstInputCooldownSources = _cooldownReadSources.ToArray();
            Inputs.Add((actor.Name, skill, Now));
            if (DropAllSkills || DropFirstSkill && Inputs.Count == 1)
            {
                WaitForSelection(hold ? 1000 : 50, default);
                return;
            }
            var cooldown = _program.GetTiming(actor.Name, skill == Method.Skill ? "e" : "q", hold)?.Cooldown
                ?? throw new InvalidDataException("生产目录缺少CD：" + actor.Name);
            _cooldowns[(actor.Name, skill)] = (Now + (skill == Method.Burst ? 2.4 : .4), Now + cooldown);
            if (skill == Method.Burst) _energyEvents.Add((Now, actor.Name, false));
            if (RockEnergyEvents && actor.Name == "班尼特" && skill == Method.Skill)
                _energyEvents.Add((Now + .8, "香菱", true));
            // 仅这个受控场景声明一次E后0.8s发生就绪事件，不声称真实粒子数或能量转化率。
            if (ResourceEvents && actor.Name == "琴")
            {
                if (skill == Method.Skill) _energyEvents.Add((Now + .8, actor.Name, true));
                else ScheduleHealth(Now + 2.4, actor.Name, low: false);
            }
            WaitForSelection(hold ? 1000 : 50, default);
        }
        public CombatBattleHostInputResult SubmitInput(NativeCombatActor actor, CombatCommand command,
            CombatNativeInputRequest request, Action begin, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            begin();
            var started = Clock.GetTimestamp();
            if (command.Method == Method.Skill || command.Method == Method.Burst) SkillRequests.Add(request.Id);
            if (command.NativeSkillSequence is { } sequence)
            {
                if (Inputs.Count == 0) FirstInputCooldownSources = _cooldownReadSources.ToArray();
                Inputs.Add((actor.Name, Method.Skill, Now));
                try
                {
                    foreach (var primitive in sequence)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (primitive.Method == Method.Wait)
                            WaitForSelection((int)(double.Parse(primitive.Args![0], System.Globalization.CultureInfo.InvariantCulture) * 1000), ct);
                        else ExecutePrimitive(actor, primitive);
                        ct.ThrowIfCancellationRequested();
                        command.NativeSkillObserver?.Invoke();
                    }
                }
                finally { _held = false; }
                if (!(DropFirstSkill && Inputs.Count == 1))
                    _cooldowns[(actor.Name, Method.Skill)] = (Now + .4, Now + _program.GetTiming(actor.Name, "e", command.HasFlag("hold"))!.Cooldown!.Value);
            }
            else if (command.Method == Method.Skill) Send(actor, Method.Skill, command.HasFlag("hold"));
            else if (command.Method == Method.Burst) Send(actor, Method.Burst, false);
            else ExecutePrimitive(actor, command);
            return new(command.Method == Method.MoveBy && MoveByReceiptFault == "unknown" ? CombatBattleHostInputStatus.Unknown : CombatBattleHostInputStatus.Sent, Clock.GetTimestamp(),
                Error: Primitives.Count == 1 ? PrimitivePostError : null)
            {
                NativeRequested = CompleteSkillReceipts ? 2 : command.Method == Method.MoveBy ? MoveByReceiptFault == "partial" ? 2 : 1 : null,
                NativeSubmitted = CompleteSkillReceipts ? 2 : command.Method == Method.MoveBy ? 1 : null,
                StartedTimestamp = CompleteSkillReceipts ? started : null
            };
        }
        public CombatBattleHostInputResult SubmitPathingInput(CombatCommand command, CombatNativeInputRequest request,
            Action begin, CancellationToken ct, CannonUiObservation scene = default)
        {
            BeforePathingInput?.Invoke();
            ct.ThrowIfCancellationRequested();
            if (!PathingPrimitiveInput.Supports(command, scene, request.Source))
                return new(CombatBattleHostInputStatus.Failed, Reason: "scene-does-not-authorize-input");
            if (command.Method == Method.Jump && ++PathingJumpAttempts == 1 && PathingJumpFault == "not-sent")
                return new(CombatBattleHostInputStatus.NotSent);
            begin();
            ExecutePrimitive(new(CombatScriptParser.CurrentAvatarName, 0), command);
            PathingPrimitives.Add(command);
            if (command.Method == Method.Jump)
            {
                PathingJumpSources.Add(request.Source);
                if (PathingJumpFault == "foreign") { _producer.Restart(); Advance(50); }
                if (PathingJumpFault is "unknown" or "partial")
                    return new(CombatBattleHostInputStatus.Unknown)
                        { NativeRequested = 2, NativeSubmitted = PathingJumpFault == "partial" ? 1 : null,
                            ObservableAfterTimestamp = Clock.GetTimestamp() };
            }
            return new(CombatBattleHostInputStatus.Sent, Clock.GetTimestamp());
        }
        public void ExecutePrimitive(NativeCombatActor actor, CombatCommand command)
        {
            Primitives.Add(command);
            if (command.Method == Method.MoveBy) MoveByHeldStates.Add(_held);
            if (HideHudAfterInteraction && command.Method == Method.KeyPress)
            {
                if (User32Helper.ToVk(command.Args![0]) == User32.VK.VK_F) _interactionUi = true;
                if (User32Helper.ToVk(command.Args![0]) == User32.VK.VK_ESCAPE) _interactionUi = false;
            }
            if (command.Method == Method.KeyDown)
            {
                _held = true;
                var key = User32Helper.ToVk(command.Args![0]);
                _heldKeys.Add(key);
                KeyEvents.Add((key, false, Now, false));
            }
            if (command.Method == Method.KeyUp)
            {
                var key = User32Helper.ToVk(command.Args![0]);
                _heldKeys.Remove(key);
                KeyEvents.Add((key, true, Now, false));
                if (_held && actor.Name == "那维莱特") HeldReleases.Add(Now);
                _held = false;
            }
            if (command.Method == Method.Attack)
            {
                FirstAttack = Math.Min(FirstAttack, Now);
                var milliseconds = (int)(double.Parse(command.Args![0], System.Globalization.CultureInfo.InvariantCulture) * 1000);
                // 生产Avatar.Attack按200ms节拍执行，包含ms==0的那一次点击及等待。
                WaitForSelection((milliseconds / 200 + 1) * 200, default);
            }
        }
        public List<bool> VisionPreparations { get; } = [];
        public Task? VisionReadiness { get; set; }
        private bool _visionInvalidated;
        public bool IsVisionPrepared => !_visionInvalidated;
        public void InvalidateVision(Task readiness) { VisionReadiness = readiness; _visionInvalidated = true; }
        public Task PrepareVisionAsync(CancellationToken ct) => PrepareVisionAsync(true, ct);
        public Task PrepareVisionAsync(bool needsBurst, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); _visionInvalidated = false; VisionPreparations.Add(needsBurst); return VisionReadiness ?? Task.CompletedTask; }
        public IDisposable BeginExclusive(bool allowPassiveObservation) => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public void ReleaseInput()
        {
            if (InputReleaseError != null) throw InputReleaseError;
            foreach (var key in _heldKeys) KeyEvents.Add((key, true, Now, true));
            _heldKeys.Clear();
            _held = false;
            AfterRelease?.Invoke();
        }
        public Func<User32.VK?>? HeldMapping { get; init; }
        public string? HeldReceipt { get; init; }
        public User32.VK? HeldEPhysicalKey() => HeldMapping == null ? User32.VK.VK_E : HeldMapping();
        public CombatBattleHostInputResult SubmitHeldE(User32.VK key, bool down, CombatNativeInputRequest request,
            Action begin, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            begin();
            ExecutePrimitive(new(_actor, 1), new CombatCommand(_actor, $"{(down ? "keydown" : "keyup")}({key})"));
            return new(HeldReceipt == "unknown" ? CombatBattleHostInputStatus.Unknown : CombatBattleHostInputStatus.Sent, Clock.GetTimestamp())
                { NativeRequested = HeldReceipt == "partial" ? 2 : 1, NativeSubmitted = 1, StartedTimestamp = Clock.GetTimestamp() };
        }
        public void ReleaseHeldE(User32.VK key)
        {
            if (InputReleaseError != null) throw InputReleaseError;
            if (_heldKeys.Remove(key)) KeyEvents.Add((key, true, Now, false));
            _held = _heldKeys.Count != 0;
        }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            if (PathingJumpFault == "wait-foreign" && PathingJumpSources.Count == 1 &&
                CombatActionScope.Current != null && !_restartedDuringWait)
            {
                _restartedDuringWait = true;
                _producer.Restart();
            }
            ct.ThrowIfCancellationRequested(); Advance(milliseconds + DelayLatenessMs); ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public void Dispose() => _snapshots.Clear();
    }
}
