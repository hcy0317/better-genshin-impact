using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatBattleHostTests
{
    [Theory]
    [InlineData("confirmed")]
    [InlineData("parent-cancelled")]
    [InlineData("not-revive")]
    [InlineData("unconfirmed")]
    [InlineData("recovery-failed")]
    public async Task ReviveSelectionAt35SecondsUsesParentRecoveryWithoutCompletingBattle(string outcome)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        using var task = TaskExecutionScope.BeginOwned();
        using var parent = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var native = new ReviveHandoffIo(clock, parent, outcome);
        using var flow = NativeCombatFlowRunner.Create(
            CombatFlowProgram.Compile("strategy(loop=battle)\n那维莱特 attack(0.1,required)"), native);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 5, 400),
                1, SeekCueKind.HealthBar), 1920, 1080);
        await host.AdvanceAsync(flow, parent.Token);
        io.TargetFactory = null;
        clock.Advance(TimeSpan.FromSeconds(20));
        for (var i = 0; i < 2000 && host.State != "Reengaging"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, parent.Token));
        Assert.Equal("Reengaging", host.State);
        var elapsed = clock.GetElapsedTime(started).TotalSeconds;
        Assert.InRange(elapsed, 20, 34.9);
        clock.Advance(TimeSpan.FromSeconds(35 - elapsed));
        native.RequireSelectionRecovery = true;

        var result = CombatBattleHostResult.Continue;
        var error = await Record.ExceptionAsync(async () =>
        {
            for (var i = 0; i < 2000 && result == CombatBattleHostResult.Continue; i++)
                result = await host.AdvanceAsync(flow, parent.Token);
        });

        Assert.InRange(native.RecoveryObservedAt, 35, 36);
        Assert.Equal(1, native.SelectionRequests);
        Assert.DoesNotContain(io.Traces, trace => trace.Result == CombatBattleHostResult.Completed);
        Assert.InRange(host.CameraRequests, 0, 24);
        Assert.InRange(host.ApproachRequests, 0, 12);
        Assert.Equal(native.InputsAtRecovery, native.Inputs);
        if (outcome == "confirmed")
        {
            Assert.IsType<CombatRecoveryCompletedException>(error);
            Assert.True(native.Recovered);
            Assert.Equal(2, native.VerificationFrames);
            Assert.True(clock.GetElapsedTime(started).TotalSeconds > 47);
            TaskExecutionScope.ThrowIfFailed();
            Assert.Equal(CombatBattleHostResult.Unconfirmed, await host.AdvanceAsync(flow, parent.Token));
            await Assert.ThrowsAsync<InvalidOperationException>(() => flow.StepAsync(parent.Token).AsTask());
            Assert.Equal(native.InputsAtRecovery, native.Inputs);
        }
        else
        {
            Assert.False(native.Recovered);
            Assert.IsNotType<CombatRecoveryCompletedException>(error);
            if (outcome == "parent-cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
            if (outcome == "recovery-failed") Assert.IsType<TimeoutException>(error);
            if (outcome == "unconfirmed")
            {
                Assert.IsType<InvalidOperationException>(error);
                Assert.Equal(1, native.VerificationFrames);
            }
            if (outcome == "not-revive")
            {
                Assert.Equal(0, native.RecoveryInputs);
                Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
            }
        }
    }

    // Only game I/O is replaced: production host, flow, selection protocol,
    // frame fences and confirmed-recovery retry signal all remain in this path.
    private sealed class ReviveHandoffIo(FakeTimeProvider clock, CancellationTokenSource parent, string outcome) : INativeCombatIo
    {
        private readonly CaptureFrameSource _source = new(clock);
        private readonly long _started = clock.GetTimestamp();
        private readonly Dictionary<CaptureFrameStamp, (bool Hud, int Active)> _frames = [];
        private bool _prompt;
        public bool RequireSelectionRecovery { get; set; }
        public int SelectionRequests { get; private set; }
        public int Inputs { get; private set; }
        public int InputsAtRecovery { get; private set; }
        public int RecoveryInputs { get; private set; }
        public int VerificationFrames { get; private set; }
        public bool Recovered { get; private set; }
        public double RecoveryObservedAt { get; private set; } = double.NaN;
        public TimeProvider Clock => clock;
        public ILogger Logger => NullLogger.Instance;
        public CombatInputCoordinator InputCoordinator { get; } = new();
        public IReadOnlyList<NativeCombatActor> Actors { get; } = [new("钟离", 1), new("那维莱特", 3)];
        public double LastFinishCheckAge => 0;
        public Task PrepareVisionAsync(CancellationToken ct) => Task.CompletedTask;
        public IDisposable BeginExclusive(bool allowPassiveObservation) => new NoopScope();
        public ImageRegion Capture()
        {
            clock.Advance(TimeSpan.FromMilliseconds(50));
            var stamp = _source.Next();
            _frames.Add(stamp, (!_prompt, RequireSelectionRecovery ? 1 : 3));
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
            SelectionRequests++;
            clock.Advance(TimeSpan.FromMilliseconds(70));
            _prompt = true;
            return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()) { NativeRequested = 2, NativeSubmitted = 2 };
        }
        public void WaitForSelection(int milliseconds, CancellationToken ct) => DelayAsync(milliseconds, ct).GetAwaiter().GetResult();
        public bool OnSelectionMismatch(NativeCombatActor actor, int attempt, int attempts, int observed, CancellationToken ct) => false;
        public void ResolveSelectionRecovery(NativeCombatActor actor, AvatarSelectionProtocol.Result<ImageRegion> selection, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (double.IsNaN(RecoveryObservedAt))
            {
                RecoveryObservedAt = clock.GetElapsedTime(_started).TotalSeconds;
                InputsAtRecovery = Inputs;
                Assert.True(selection.NeedsRecovery);
                Assert.NotNull(selection.BorrowFrame);
                Assert.True(selection.InputFence?.Accepts(selection.Source) == true);
                using var before = selection.TakeBefore();
                Assert.NotNull(before);
                Assert.True(IsCombatHud(before));
                Assert.Equal(1, ReadActive(before, new()));
            }
            // Missing HUD alone is not revive evidence and must not start recovery.
            if (outcome == "not-revive") return;
            ct = CombatRecoveryOwner.Start(ct);
            CombatRecoveryCompletedException.RecoverAsync(() =>
            {
                RecoveryInputs++;
                clock.Advance(TimeSpan.FromSeconds(outcome == "parent-cancelled" ? 3 : 12));
                if (outcome == "parent-cancelled") parent.Cancel();
                ct.ThrowIfCancellationRequested();
                if (outcome == "recovery-failed") throw new TimeoutException("recorded statue recovery failed");
                _prompt = false;
                RequireSelectionRecovery = outcome == "unconfirmed";
                return Task.CompletedTask;
            }, () =>
            {
                using var frame = Capture();
                VerificationFrames++;
                var observed = new ReviveRecoveryFrame(frame.FrameStamp.Sequence, IsMainUi(frame), ReviveUiState.None,
                    ReadActive(frame, new()), new Dictionary<int, string> { [1] = "钟离", [3] = "那维莱特" })
                    .WithSource(frame.FrameStamp, clock);
                var confirmed = observed.Confirms(new(actor.Name, actor.Index));
                if (VerificationFrames == 2 && confirmed) Recovered = true;
                return confirmed;
            }, () => DelayAsync(50, ct), ct).GetAwaiter().GetResult();
        }
        public void CheckDefeated(ImageRegion frame, CancellationToken ct) => ct.ThrowIfCancellationRequested();
        public CombatBattleHostInputResult SubmitInput(NativeCombatActor actor, CombatCommand command,
            CombatNativeInputRequest request, Action beforeFirstNative, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            beforeFirstNative();
            Inputs++;
            clock.Advance(TimeSpan.FromMilliseconds(100));
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
