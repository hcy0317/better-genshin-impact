using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class PathingMacroRecoveryOwnerTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedPathingReviveKeepsParentRecoveryWithoutCompletingOrReenteringOldFlow(bool fullFlow)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        using var task = TaskExecutionScope.BeginOwned();
        using var parent = new CancellationTokenSource();
        var macroIo = new LegacyPathingMacroTests.MacroReplay { Scene = PathingMacroScene.World };
        var script = CombatScriptParser.ParseContext("那维莱特 attack(0.1)", false);
        var plan = LegacyPathingMacroPlan.Create(script, ["钟离", "那维莱特"]);
        Assert.Single(plan.Segments);
        Assert.False(plan.Segments[0].IsRaw);
        if (fullFlow) script.CombatCommands[0].Flags.Add("required");
        Assert.Equal(fullFlow, script.HasFlowCommands);
        var native = new ReviveIo(macroIo.Time, macroIo.Coordinator, parent, plan.BudgetSeconds);
        using var macro = new PathingMacroSession(macroIo);
        NativeCombatFlowRunner? flow = null;
        CombatExecutionResult? macroResult = null;
        try
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                macroResult = await CombatScriptExecutor.ExecutePathingAsync(script, macro,
                    () =>
                    {
                        Assert.False(fullFlow, "Full flow must not enumerate the legacy macro party.");
                        return new[] { "钟离", "那维莱特" };
                    }, commands =>
                    {
                        flow = NativeCombatFlowRunner.Create(commands, native, false,
                            CombatScriptExecutionMode.LegacyPartyTemplate, purpose: CombatScriptExecutionPurpose.Pathing);
                        return flow;
                    }, parent.Token);
            });

            output.WriteLine($"replay fullFlow={fullFlow} baselineMacroBudget={plan.BudgetSeconds:F3}s elapsed={native.Elapsed:F3}s " +
                $"reviveStarted={native.RecoveryStarted} callerCancelled={parent.IsCancellationRequested} " +
                $"executionCancelled={native.ExecutionTokenAtRecovery.IsCancellationRequested} " +
                $"recoveryUsesParent={native.RecoveryUsesParent} recognitionIsExecutionToken={native.RecognitionIsExecutionToken} " +
                $"recognitionCancelled={native.RecognitionCancelled} recoveryRemaining={native.RecoveryRemaining:F3}s");
            Assert.True(native.RecoveryStarted);
            Assert.False(parent.IsCancellationRequested);
            Assert.True(native.Elapsed > plan.BudgetSeconds);
            Assert.True(native.RecoveryRemaining > 0);
            Assert.True(native.RecoveryUsesParent);
            Assert.False(native.RecognitionCancelled);
            Assert.Equal(!fullFlow, native.ExecutionTokenAtRecovery.IsCancellationRequested);
            Assert.IsType<CombatRecoveryCompletedException>(error);
            Assert.Null(macroResult);
            Assert.True(native.Recovered);
            Assert.Equal(2, native.VerificationFrames);
            Assert.Equal(native.InputsAtRecovery, native.Inputs);
            Assert.NotNull(flow);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => flow!.StepAsync(parent.Token).AsTask());
            Assert.Equal(native.InputsAtRecovery, native.Inputs);
        }
        finally { flow?.Dispose(); }
    }

    [Fact]
    public async Task ParentCancellationAfterReviveHandoffStillCancelsDirectMacroWithoutCompletion()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        using var task = TaskExecutionScope.BeginOwned();
        using var parent = new CancellationTokenSource();
        var macroIo = new LegacyPathingMacroTests.MacroReplay { Scene = PathingMacroScene.World };
        var script = CombatScriptParser.ParseContext("那维莱特 attack(0.1)", false);
        var plan = LegacyPathingMacroPlan.Create(script, ["钟离", "那维莱特"]);
        var native = new ReviveIo(macroIo.Time, macroIo.Coordinator, parent, plan.BudgetSeconds)
            { CancelParentDuringRecovery = true };
        using var macro = new PathingMacroSession(macroIo);
        NativeCombatFlowRunner? flow = null;
        CombatExecutionResult? macroResult = null;
        try
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                macroResult = await CombatScriptExecutor.ExecutePathingAsync(script, macro,
                    () => new[] { "钟离", "那维莱特" }, commands =>
                    {
                        flow = NativeCombatFlowRunner.Create(commands, native, false,
                            CombatScriptExecutionMode.LegacyPartyTemplate, purpose: CombatScriptExecutionPurpose.Pathing);
                        return flow;
                    }, parent.Token);
            });

            output.WriteLine($"replay parent cancellation after handoff: elapsed={native.Elapsed:F3}s " +
                $"reviveStarted={native.RecoveryStarted} recoveryUsesParent={native.RecoveryUsesParent} " +
                $"callerCancelled={parent.IsCancellationRequested} recognitionCancelled={native.RecognitionCancelled} " +
                $"recoveryRemaining={native.RecoveryRemaining:F3}s verificationFrames={native.VerificationFrames}");
            Assert.True(native.RecoveryStarted);
            Assert.True(native.RecoveryUsesParent);
            Assert.True(parent.IsCancellationRequested);
            Assert.True(native.RecognitionCancelled);
            Assert.True(native.RecoveryRemaining > 0);
            Assert.IsAssignableFrom<OperationCanceledException>(error);
            Assert.IsNotType<CombatRecoveryCompletedException>(error);
            Assert.Null(macroResult);
            Assert.False(native.Recovered);
            Assert.Equal(0, native.VerificationFrames);
            Assert.Equal(native.InputsAtRecovery, native.Inputs);
            Assert.NotNull(flow);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => flow!.StepAsync(parent.Token).AsTask());
            Assert.Equal(native.InputsAtRecovery, native.Inputs);
        }
        finally { flow?.Dispose(); }
    }

    [Fact]
    public async Task OrdinaryNativePreparationCannotOutliveOriginalMacroDeadline()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        using var task = TaskExecutionScope.BeginOwned();
        using var parent = new CancellationTokenSource();
        var macroIo = new LegacyPathingMacroTests.MacroReplay { Scene = PathingMacroScene.World };
        var script = CombatScriptParser.ParseContext("那维莱特 attack(0.1)", false);
        var plan = LegacyPathingMacroPlan.Create(script, ["钟离", "那维莱特"]);
        var native = new ReviveIo(macroIo.Time, macroIo.Coordinator, parent, plan.BudgetSeconds)
            { ExhaustNativePreparationBudget = true };
        using var macro = new PathingMacroSession(macroIo);
        CombatExecutionResult? macroResult = null;

        var error = await Record.ExceptionAsync(async () =>
        {
            macroResult = await CombatScriptExecutor.ExecutePathingAsync(script, macro,
                () => new[] { "钟离", "那维莱特" }, commands => NativeCombatFlowRunner.Create(
                    commands, native, false, CombatScriptExecutionMode.LegacyPartyTemplate,
                    purpose: CombatScriptExecutionPurpose.Pathing), parent.Token);
        });

        output.WriteLine($"replay ordinary native preparation: planBudget={plan.BudgetSeconds:F3}s " +
            $"elapsed={native.Elapsed:F3}s preparationCancelled={native.PreparationCancelled} " +
            $"callerCancelled={parent.IsCancellationRequested} reviveStarted={native.RecoveryStarted} inputs={native.Inputs}");
        Assert.True(native.PreparationCancelled);
        Assert.False(parent.IsCancellationRequested);
        Assert.InRange(native.Elapsed, plan.BudgetSeconds, plan.BudgetSeconds + .1);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.IsNotType<CombatRecoveryCompletedException>(error);
        Assert.Null(macroResult);
        Assert.False(native.RecoveryStarted);
        Assert.Equal(0, native.VerificationFrames);
        Assert.Equal(0, native.Inputs);
    }

    [Fact]
    public async Task UnconfirmedReviveInDirectMacroCannotPublishRecoveryCompleted()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        using var task = TaskExecutionScope.BeginOwned();
        using var parent = new CancellationTokenSource();
        var macroIo = new LegacyPathingMacroTests.MacroReplay { Scene = PathingMacroScene.World };
        var script = CombatScriptParser.ParseContext("那维莱特 attack(0.1)", false);
        var plan = LegacyPathingMacroPlan.Create(script, ["钟离", "那维莱特"]);
        var native = new ReviveIo(macroIo.Time, macroIo.Coordinator, parent, plan.BudgetSeconds)
            { LeaveTargetUnconfirmed = true };
        using var macro = new PathingMacroSession(macroIo);
        NativeCombatFlowRunner? flow = null;
        CombatExecutionResult? macroResult = null;
        try
        {
            var error = await Record.ExceptionAsync(async () =>
            {
                macroResult = await CombatScriptExecutor.ExecutePathingAsync(script, macro,
                    () => new[] { "钟离", "那维莱特" }, commands =>
                    {
                        flow = NativeCombatFlowRunner.Create(commands, native, false,
                            CombatScriptExecutionMode.LegacyPartyTemplate, purpose: CombatScriptExecutionPurpose.Pathing);
                        return flow;
                    }, parent.Token);
            });

            output.WriteLine($"replay unconfirmed recovery: elapsed={native.Elapsed:F3}s " +
                $"reviveStarted={native.RecoveryStarted} recoveryUsesParent={native.RecoveryUsesParent} " +
                $"callerCancelled={parent.IsCancellationRequested} recognitionCancelled={native.RecognitionCancelled} " +
                $"recoveryRemaining={native.RecoveryRemaining:F3}s verificationFrames={native.VerificationFrames}");
            Assert.True(native.RecoveryStarted);
            Assert.True(native.RecoveryUsesParent);
            Assert.False(parent.IsCancellationRequested);
            Assert.False(native.RecognitionCancelled);
            Assert.True(native.Elapsed > plan.BudgetSeconds);
            Assert.True(native.RecoveryRemaining > 0);
            Assert.IsType<InvalidOperationException>(error);
            Assert.IsNotType<CombatRecoveryCompletedException>(error);
            Assert.Null(macroResult);
            Assert.False(native.Recovered);
            Assert.Equal(1, native.VerificationFrames);
            Assert.Equal(native.InputsAtRecovery, native.Inputs);
            Assert.NotNull(flow);
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => flow!.StepAsync(parent.Token).AsTask());
            Assert.Equal(native.InputsAtRecovery, native.Inputs);
        }
        finally { flow?.Dispose(); }
    }

    private sealed class ReviveIo(FakeTimeProvider clock, CombatInputCoordinator coordinator,
        CancellationTokenSource parent, double macroBudgetSeconds) : INativeCombatIo
    {
        private readonly CaptureFrameSource _source = new(clock);
        private readonly long _started = clock.GetTimestamp();
        private readonly Dictionary<CaptureFrameStamp, (bool Hud, int Active)> _frames = [];
        private bool _prompt, _restored;
        public CancellationToken ExecutionTokenAtRecovery { get; private set; }
        public bool RecoveryStarted { get; private set; }
        public bool CancelParentDuringRecovery { get; init; }
        public bool LeaveTargetUnconfirmed { get; init; }
        public bool ExhaustNativePreparationBudget { get; init; }
        public bool PreparationCancelled { get; private set; }
        public bool RecoveryUsesParent { get; private set; }
        public bool RecognitionIsExecutionToken { get; private set; }
        public bool RecognitionCancelled { get; private set; }
        public double RecoveryRemaining { get; private set; }
        public double Elapsed => clock.GetElapsedTime(_started).TotalSeconds;
        public bool Recovered { get; private set; }
        public int VerificationFrames { get; private set; }
        public int Inputs { get; private set; }
        public int InputsAtRecovery { get; private set; }
        public TimeProvider Clock => clock;
        public ILogger Logger => NullLogger.Instance;
        public CombatInputCoordinator InputCoordinator => coordinator;
        public IReadOnlyList<NativeCombatActor> Actors { get; } = [new("钟离", 1), new("那维莱特", 3)];
        public double LastFinishCheckAge => 0;
        public Task PrepareVisionAsync(CancellationToken ct)
        {
            if (ExhaustNativePreparationBudget)
            {
                while (!ct.IsCancellationRequested && Elapsed < macroBudgetSeconds + 1)
                    clock.Advance(TimeSpan.FromMilliseconds(50));
                PreparationCancelled = ct.IsCancellationRequested;
                ct.ThrowIfCancellationRequested();
            }
            return Task.CompletedTask;
        }
        public IDisposable BeginExclusive(bool allowPassiveObservation) => new NoopScope();
        public ImageRegion Capture()
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            var stamp = _source.Next();
            _frames.Add(stamp, (!_prompt, _restored ? 3 : 1));
            return new(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = stamp };
        }
        public bool IsCombatHud(ImageRegion frame) => _frames[frame.FrameStamp].Hud;
        public bool IsMainUi(ImageRegion frame) => IsCombatHud(frame);
        public CombatControlObservation ReadControl(ImageRegion frame) => new(MotionStatus.Unknown, false);
        public int ReadActive(ImageRegion frame, AvatarActiveCheckContext context) => _frames[frame.FrameStamp].Active;
        public bool? IsActorActive(NativeCombatActor actor, ImageRegion frame) => ReadActive(frame, new()) == actor.Index;
        public double ReadSkillCooldown(NativeCombatActor actor, ImageRegion frame) => 0;
        public bool IsSkillReady(NativeCombatActor actor, ImageRegion frame, double cooldown) => true;
        public BurstObservation ReadBurst(ImageRegion frame, bool active) => default;
        public bool? ReadLowHp(ImageRegion frame) => false;
        public HashSet<int> ReadSideBurstReady(ImageRegion frame) => [];
        public bool TryGetKnownSkillCooldown(string actor, out double cooldown) { cooldown = 0; return false; }
        public void ConfirmSkill(NativeCombatActor actor, double cooldown, DateTime inputAtUtc) { }
        public Task WaitSkillCooldown(NativeCombatActor actor, CancellationToken ct) => Task.CompletedTask;
        public CombatBattleHostInputResult SelectActor(int index, CombatNativeInputRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal(3, index);
            Inputs++;
            clock.Advance(TimeSpan.FromMilliseconds(70));
            _prompt = true;
            return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()) { NativeRequested = 2, NativeSubmitted = 2 };
        }
        public void WaitForSelection(int milliseconds, CancellationToken ct) => DelayAsync(milliseconds, ct).GetAwaiter().GetResult();
        public bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken ct) => false;
        public void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<ImageRegion> selection, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(_prompt);
            Assert.True(selection.NeedsRecovery);
            Assert.True(selection.InputFence?.Accepts(selection.Source) == true);
            using var before = selection.TakeBefore();
            Assert.NotNull(before);
            Assert.True(IsCombatHud(before));
            RecoveryStarted = true;
            InputsAtRecovery = Inputs;
            ExecutionTokenAtRecovery = ct;
            ct = CombatRecoveryOwner.Start(ct);
            RecoveryUsesParent = ct == parent.Token;
            using var operation = UiOperation.Begin("selection-recovery", TimeSpan.FromSeconds(60), ct, clock: clock);
            operation.Check();
            RecognitionIsExecutionToken = RecognitionExecutionScope.Token == ExecutionTokenAtRecovery;
            CombatRecoveryCompletedException.RecoverAsync(() =>
            {
                var untilBeforeMacroDeadline = macroBudgetSeconds - 1 - Elapsed;
                if (untilBeforeMacroDeadline > 0) clock.Advance(TimeSpan.FromSeconds(untilBeforeMacroDeadline));
                clock.Advance(TimeSpan.FromSeconds(2));
                RecoveryRemaining = operation.Remaining.TotalSeconds;
                Assert.False(parent.IsCancellationRequested);
                if (CancelParentDuringRecovery)
                {
                    Assert.True(RecoveryUsesParent);
                    Assert.False(RecognitionExecutionScope.Token.IsCancellationRequested);
                    parent.Cancel();
                }
                RecognitionCancelled = RecognitionExecutionScope.Token.IsCancellationRequested;
                RecognitionExecutionScope.Token.ThrowIfCancellationRequested();
                _prompt = false;
                _restored = !LeaveTargetUnconfirmed;
                return Task.CompletedTask;
            }, () =>
            {
                using var frame = Capture();
                VerificationFrames++;
                var observation = new ReviveRecoveryFrame(frame.FrameStamp.Sequence, IsMainUi(frame), ReviveUiState.None,
                    ReadActive(frame, new()), new Dictionary<int, string> { [1] = "钟离", [3] = "那维莱特" })
                    .WithSource(frame.FrameStamp, clock);
                var confirmed = observation.Confirms(new(actor.Name, actor.Index));
                if (VerificationFrames == 2 && confirmed) Recovered = true;
                return confirmed;
            }, () => DelayAsync(50, operation.Token), operation.Token).GetAwaiter().GetResult();
        }
        public void CheckDefeated(ImageRegion frame, CancellationToken ct) => ct.ThrowIfCancellationRequested();
        public CombatBattleHostInputResult SubmitInput(NativeCombatActor actor, CombatCommand command,
            CombatNativeInputRequest request, Action beforeFirstNative, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            beforeFirstNative();
            Inputs++;
            return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()) { NativeRequested = 2, NativeSubmitted = 2 };
        }
        public void ReleaseInput() { }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        private sealed class NoopScope : IDisposable { public void Dispose() { } }
    }
}
