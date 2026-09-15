using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using Fischless.GameCapture;
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
        { ControlAt = at => at is >= .1 and < .5 };
        var adapter = NativeCombatFlowRunner.CreateAdapter(io);
        using var owner = (IDisposable)adapter;
        using var context = new CombatFlowContext(clock);
        var old = new CombatFlowAction(new CombatCommand("琴", "e"), context, () => true, context.Now + 4);
        adapter.BeginStep();
        Assert.Equal(CombatObservationPreparation.AwaitingObservation, await adapter.PrepareObservationStepAsync(old, "onfield", default));
        Assert.Single(io.Selections);
        await io.DelayAsync(200, default);
        adapter.BeginStep();
        Assert.Equal(CombatObservationPreparation.AwaitingObservation, await adapter.PrepareObservationStepAsync(old, "onfield", default));
        adapter.CancelObservation(old);
        var next = new CombatFlowAction(new CombatCommand("芙宁娜", "e"), context, () => true, context.Now + 4);
        while (context.Now < .8)
        {
            adapter.BeginStep();
            await adapter.PrepareObservationStepAsync(next, "onfield", default);
            await io.DelayAsync(50, default);
        }
        Assert.Single(io.Selections); // 不得把旧切人丢弃后立即向另一个角色发请求。
        var result = CombatObservationPreparation.AwaitingObservation;
        for (var i = 0; i < 80 && result == CombatObservationPreparation.AwaitingObservation; i++)
        {
            adapter.BeginStep();
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

    private sealed class HostDevice(FakeTimeProvider clock) : ICombatHostInputDevice
    {
        public TimeProvider Clock => clock;
        public ILogger Logger { get; init; } = NullLogger.Instance;
        public int Preparations { get; private set; }
        public List<string> Inputs { get; } = [];
        public Action? AfterParty { get; init; }
        public void PrepareInput() => Preparations++;
        public void MoveCamera(int x, int y) => Inputs.Add("camera");
        public void MoveForward(bool down) => Inputs.Add(down ? "forward-down" : "forward-up");
        public void PressDrop() => Inputs.Add("drop");
        public void PressParty()
        {
            Inputs.Add("party");
            clock.Advance(TimeSpan.FromMilliseconds(10));
            AfterParty?.Invoke();
        }
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); return ValueTask.CompletedTask; }
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

    private sealed class SceneReplayIo(FakeTimeProvider clock, Guid battleId) : ICombatBattleHostIo
    {
        private readonly CaptureFrameSource _source = new(clock);
        public TimeProvider Clock => clock;
        public Guid BattleId => battleId;
        public CombatBattleObservation ObserveTarget() => new(_source.Next(), battleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber),
            1920, 1080, (ulong)(clock.GetUtcNow().ToUnixTimeMilliseconds() / 1000))
        { Motion = BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal };
        public PartySetupFinishObservation ObservePartyBar() => throw new InvalidOperationException("有效目标输出阶段不得打开编队");
        public ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct) => throw new InvalidOperationException("有效目标输出阶段不得抢占输入");
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
        public double FirstShieldConfirmed { get; private set; } = double.PositiveInfinity;
        public Action? AfterCapture { get; set; }
        public double? HeldCaptureCost { get; init; }
        public string? HeldFrameFault { get; init; }
        private CaptureFrameStamp _lastDelivered;
        public List<int> Selections { get; } = [];
        public double FirstAttack { get; private set; } = double.PositiveInfinity;
        public void PrimeKnownSkillCooldown(string actor, double seconds) => _knownECdUntil[actor] = Now + seconds;
        public void PrimeSkillCooldown(string actor, double seconds)
        {
            _cooldowns[(actor, Method.Skill)] = (Now, Now + seconds);
            PrimeKnownSkillCooldown(actor, seconds);
        }
        public bool HoldingInput => _held;
        public bool FreezeWhileHeld { get; init; }
        public int RecoveryPulsesRequired { get; init; }
        public double ControlStartsAt { get; init; }
        public int ControlPulses { get; private set; }
        public bool FailControlPulse { get; init; }
        public double ControlPrepareDelay { get; init; }
        private ICombatHostInputDevice? _controlDevice;
        public ICombatHostInputDevice ControlDevice => _controlDevice ??= new ReplayControlDevice(this);
        public Func<double, bool>? ControlAt { get; init; }
        public BetterGenshinImpact.GameTask.Common.BgiVision.CombatControlObservation ReadControl(ImageRegion frame) =>
            new(BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Unknown, Frame(frame).Controlled);
        public List<ImageRegion> CapturedFrames { get; } = [];
        public bool ResourceEvents { get; init; }
        public bool DropFirstSkill { get; init; }
        public bool RockEnergyEvents { get; init; }
        public double FirstRockDemandAt { get; private set; } = double.PositiveInfinity;
        public void SetFrontActor(string actor) { _actor = actor; _selected = null; }
        public void ScheduleHealth(double at, string actor, bool low) => _healthEvents.Add((at, actor, low));
        public void ScheduleEnergy(double at, string actor, bool full) => _energyEvents.Add((at, actor, full));
        public int SelectionWaitsBeforeFirstSkill { get; private set; }
        private double Now => _clock.GetElapsedTime(_started).TotalSeconds;
        private sealed record Snapshot(string? Actor, double At, bool Controlled);

        public TimeProvider Clock => _clock;
        public Microsoft.Extensions.Logging.ILogger Logger => Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        public CombatInputCoordinator InputCoordinator { get; } = new();
        public IReadOnlyList<NativeCombatActor> Actors { get; init; } = new[]
        { new NativeCombatActor("钟离", 1), new("芙宁娜", 2), new("那维莱特", 3), new("琴", 4) };
        public double LastFinishCheckAge => 0;

        public PhysicalReplay(FakeTimeProvider clock, bool burstReady, double cost, CombatFlowProgram program)
        {
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
            var wait = _clock.GetElapsedTime(_clock.GetTimestamp(), _nextFrameTimestamp).TotalMilliseconds;
            Advance(wait);
            var source = _latest;
            if (_held && HeldFrameFault == "frozen") source = _lastDelivered;
            if (_held && HeldFrameFault == "future") source = source with { CapturedTimestamp = _clock.GetTimestamp() + _clock.TimestampFrequency };
            if (_held && HeldFrameFault == "restart") { _producer.Restart(); source = _producer.Next(); }
            var at = _clock.GetElapsedTime(_started, source.CapturedTimestamp).TotalSeconds;
            var animation = _cooldowns.TryGetValue((_actor, Method.Burst), out var burst) && at < burst.Visible;
            _snapshots[source] = new(animation ? null : _held && HeldFrameFault == "foreign-actor" ? "琴" : _actor, at,
                FreezeWhileHeld && _held || ControlAt?.Invoke(at) == true || at >= ControlStartsAt && ControlPulses < RecoveryPulsesRequired);
            _lastDelivered = source;
            Advance(Math.Max(0, (_held ? HeldCaptureCost ?? _cost : _cost) - wait));
            AfterCapture?.Invoke();
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
            public void MoveForward(bool down) => throw new InvalidOperationException("控制恢复不能移动");
            public void PressDrop() => throw new InvalidOperationException("控制恢复不能退出/脱离");
            public void PressParty() => throw new InvalidOperationException("控制恢复不能打开编队");
            public void PressBreakout()
            {
                owner.ControlPulses++; owner.Advance(10);
                if (owner.FailControlPulse) throw new IOException("partial control input");
            }
            public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => new(owner.DelayAsync(milliseconds, ct));
        }
        public bool IsCombatHud(ImageRegion frame) => Frame(frame).Actor != null;
        public bool IsMainUi(ImageRegion frame) => IsCombatHud(frame);
        public int ReadActive(ImageRegion frame, AvatarActiveCheckContext context) =>
            Actors.FirstOrDefault(actor => actor.Name == Frame(frame).Actor)?.Index ?? -1;
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
            return IsActorActive(actor, frame) == true && cooldown <= 0;
        }
        public BurstObservation ReadBurst(ImageRegion frame, bool active)
        {
            var sample = Frame(frame);
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
        public void SelectActor(int index, CancellationToken ct)
        {
            Selections.Add(index);
            ct.ThrowIfCancellationRequested();
            var actor = Actors.Single(item => item.Index == index).Name;
            if (_selected == null && _actor != actor) { _selected = actor; _selectionCompletes = Now + 1; }
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
            if (DropFirstSkill && Inputs.Count == 1)
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
        public void SendSkill(NativeCombatActor actor, bool hold) => Send(actor, Method.Skill, hold);
        public void SendBurst(NativeCombatActor actor) => Send(actor, Method.Burst, false);
        public void ExecutePrimitive(NativeCombatActor actor, CombatCommand command)
        {
            Primitives.Add(command);
            if (command.Method == Method.KeyDown) _held = true;
            if (command.Method == Method.KeyUp)
            {
                if (_held && actor.Name == "那维莱特") HeldReleases.Add(Now);
                _held = false;
            }
            if (command.Method == Method.Attack)
            {
                FirstAttack = Math.Min(FirstAttack, Now);
                WaitForSelection((int)(double.Parse(command.Args![0], System.Globalization.CultureInfo.InvariantCulture) * 1000), default);
            }
        }
        public Task PrepareVisionAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public IDisposable BeginExclusive(bool allowPassiveObservation) => new Lease();
        private sealed class Lease : IDisposable { public void Dispose() { } }
        public void ReleaseInput() => _held = false;
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Advance(milliseconds); ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        public void Dispose() => _snapshots.Clear();
    }
}
