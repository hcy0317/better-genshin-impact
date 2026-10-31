using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 0)]
    [InlineData(true, 266)]
    public async Task ExtremeInitialPitchNeedsPhysicalResetBeforeTargetAndIndependentPartyCompletion(bool resetEffective, int resetReleaseDelayMilliseconds)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 attack(.1)");
        using var physical = new PhysicalReplay(clock, false, 20, program) { ExternalClockAdvance = true };
        physical.SetFrontActor("琴");
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new PitchSceneDevice(clock)
            { ResetEffective = resetEffective, ResetReleaseDelayMilliseconds = resetReleaseDelayMilliseconds };
        var source = new CaptureFrameSource(clock);
        var traces = new List<CombatBattleHostTrace>();
        var visibleAt = -1;
        var healthFrames = 0;
        var targetDefeated = false;
        device.CanOpenParty = () => targetDefeated;
        CombatBattleObservation Observe()
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            var stamp = source.Next();
            var attacks = physical.Primitives.Count(command => command.Method == Method.Attack);
            // A fixed world target at the horizon. Camera input changes the physical pose;
            // visibility follows projection, independently of the host's scan policy.
            var elevation = -device.PitchDegrees * Math.PI / 180;
            var projectedY = 270 - Math.Tan(elevation) / Math.Tan(Math.PI / 6) * 270;
            using var frame = new ImageRegion(new Mat(540, 960, MatType.CV_8UC3, new Scalar(30, 70, 30)), 0, 0)
                { FrameStamp = stamp };
            if (projectedY is > 60 and < 460)
            {
                if (visibleAt < 0) visibleAt = attacks;
                targetDefeated = attacks - visibleAt >= 3;
                if (!targetDefeated)
                    Cv2.Rectangle(frame.SrcMat, new Rect(440, (int)projectedY, 80 - (attacks - visibleAt) * 20, 6),
                        new Scalar(90, 90, 255), -1);
            }
            var evidence = AvatarRecognition.ReadPassiveTargetEvidence(frame);
            EnemySeekDecision? target = evidence.Decision.SignalCount > 0 ? evidence.Decision : null;
            if (target?.Cue == SeekCueKind.HealthBar) healthFrames++;
            return new(stamp, flow.Context.BattleId, device.PartyOpen || !physical.PassiveObservationAvailable ? CombatObservationQuality.Unavailable : CombatObservationQuality.Available,
                target, 960, 540)
            { Control = new(MotionStatus.Unknown, false), Recognition = evidence.Diagnostics };
        }
        PartySetupFinishObservation Party()
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            var stamp = source.Next();
            return new(stamp.Sequence, stamp.CapturedAt, 960, 540, device.PartyOpen && targetDefeated, (ulong)stamp.Sequence)
                { Source = stamp };
        }
        var native = new NativeCombatBattleHostIo(flow, device, Party, Observe);
        var io = new TracedPitchIo(native, traces);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1, TimeoutSeconds = 100 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 2000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);

        output.WriteLine($"effective={resetEffective} result={result} healthFrames={healthFrames} pitch={device.PitchDegrees:F2} resets={device.ResetCount} scans={host.CameraRequests} reason={host.Reason}");
        foreach (var trace in traces.TakeLast(12))
            output.WriteLine($"state={trace.State} reason={trace.Reason} input={trace.InputKind}/{trace.InputStatus} gate={trace.ObservationGate} party={trace.PartyReason} target={trace.Observation.Target?.Cue} progress={trace.LastProgress?.Kind}");
        if (resetEffective)
        {
            Assert.Equal(CombatBattleHostResult.Completed, result);
            Assert.True(healthFrames > 0, "The existing relative scan cannot bring an 85-degree initial pitch into the target's field of view.");
            Assert.True(targetDefeated);
            Assert.Equal(1, device.ResetCount);
            Assert.Contains(traces, trace => trace.LastProgress?.Kind == "health-progress");
            Assert.Contains(traces, trace => trace.PartyReason == "confirmed");
            Assert.DoesNotContain(traces, trace => trace.Result == CombatBattleHostResult.Completed && trace.PartyReason != "confirmed");
        }
        else
        {
            Assert.Equal(0, healthFrames);
            Assert.True(device.MinimumPitchDegrees > 60);
            Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
            Assert.DoesNotContain(traces, trace => trace.LastProgress != null || trace.Result == CombatBattleHostResult.Completed);
            Assert.Equal(24, traces.Max(trace => trace.ScanUsed));
        }
        var reset = Assert.Single(io.Receipts.Where(receipt => receipt.Input.Kind == CombatBattleHostInputKind.CameraReset));
        Assert.Equal(2, reset.Result.NativeRequested);
        Assert.Equal(2, reset.Result.NativeSubmitted);
        Assert.Null(reset.Result.Error);
        if (resetReleaseDelayMilliseconds > 0) Assert.NotNull(reset.Result.PerformanceRecheck);
        var rechecked = Assert.Single(traces.Where(trace => trace.Reason == "host-camera-reset-rechecked"));
        var observableAt = Math.Max(reset.Result.CompletedTimestamp!.Value,
            reset.Result.ObservableAfterTimestamp ?? reset.Result.CompletedTimestamp.Value);
        Assert.True(clock.GetElapsedTime(observableAt, rechecked.Observation.Source.CapturedTimestamp).TotalMilliseconds >= 500,
            "Camera reset must settle for 500ms before its new frame can release the host barrier.");
        Assert.True(rechecked.Observation.Source.IsAfter(reset.Input.Source));
        Assert.Null(rechecked.LastProgress);
        flow.Dispose();
        Assert.All(physical.CapturedFrames, frame => Assert.True(frame.SrcMat.IsDisposed));
        Assert.False(device.ResetHeld);
        Assert.InRange(traces.Max(trace => trace.ScanUsed), 0, 24);
        Assert.InRange(traces.Max(trace => trace.ApproachUsed), 0, 12);
    }

    [Theory]
    [InlineData("menu")]
    [InlineData("owned-party")]
    [InlineData("stale")]
    [InlineData("foreign-battle")]
    [InlineData("foreign-source")]
    [InlineData("unobserved-control")]
    [InlineData("breakout")]
    [InlineData("swim")]
    public async Task NativeCameraResetRequiresFreshPlayableWorldAndObservedControl(string invalid)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 20, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new PitchSceneDevice(clock);
        var source = new CaptureFrameSource(clock);
        var stamp = source.Next();
        var observation = new CombatBattleObservation(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 960, 540)
            { Control = new(MotionStatus.Unknown, false) };
        observation = invalid switch
        {
            "menu" => observation with { Quality = CombatObservationQuality.Unavailable },
            "owned-party" => observation,
            "stale" => observation with { Source = stamp with { CapturedTimestamp = stamp.CapturedTimestamp - clock.TimestampFrequency } },
            "foreign-battle" => observation with { BattleId = Guid.NewGuid() },
            "foreign-source" => observation with { Source = new CaptureFrameSource(clock).Next() },
            "unobserved-control" => observation with { Control = default },
            "breakout" => observation with { Control = new(MotionStatus.Unknown, true) },
            "swim" => observation with { Motion = MotionStatus.Swim, Control = new(MotionStatus.Swim, false) },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        var native = new NativeCombatBattleHostIo(flow, device, targetObservation: () => observation);
        if (invalid == "owned-party")
            Assert.Equal(CombatBattleHostInputStatus.Sent, (await native.SendAsync(new(CombatBattleHostInputKind.OpenParty)
                { RequestId = Guid.NewGuid(), Source = stamp, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency }, default)).Status);
        var result = await native.SendAsync(new(CombatBattleHostInputKind.CameraReset)
            { RequestId = Guid.NewGuid(), Source = stamp, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency }, default);
        Assert.Equal(CombatBattleHostInputStatus.NotSent, result.Status);
        Assert.Equal(0, device.ResetCount);
        Assert.Equal(0, device.ResetAttempts);
        native.ReleaseInput();
    }

    [Fact]
    public async Task NativeCameraResetCompleteAfterPerformanceBudgetKeepsItsOriginalRequestForRecheck()
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 20, program) { ExternalClockAdvance = true };
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new PitchSceneDevice(clock) { ResetReleaseDelayMilliseconds = 266 };
        var stamp = new CaptureFrameSource(clock).Next();
        var native = new NativeCombatBattleHostIo(flow, device, targetObservation: () =>
            new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 960, 540)
                { Control = new(MotionStatus.Unknown, false) });
        var request = new CombatBattleHostInput(CombatBattleHostInputKind.CameraReset)
        {
            RequestId = Guid.NewGuid(), Source = stamp,
            DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency * 5
        };

        var result = await native.SendAsync(request, default);

        output.WriteLine($"status={result.Status} reason={result.Reason} requested={result.NativeRequested} submitted={result.NativeSubmitted} uncertain={result.NativeUncertain} error={result.Error}");
        Assert.False(device.ResetHeld);
        Assert.Equal(1, device.ResetCount);
        Assert.Equal(2, device.ResetAttempts);
        Assert.Equal(2, result.NativeRequested);
        Assert.Equal(2, result.NativeSubmitted);
        Assert.False(result.NativeUncertain);
        Assert.Equal(new[] { 40 }, device.Delays);
        Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
        Assert.Null(result.Error);
        Assert.NotNull(result.PerformanceRecheck);
        var recheck = result.PerformanceRecheck.Value;
        Assert.Equal(request.RequestId, recheck.RequestId);
        Assert.Equal(request.Source, recheck.Source);
        Assert.Equal(request.DeadlineTimestamp, recheck.DeadlineTimestamp);
        Assert.Null(recheck.SelectionGoal);
        Assert.True(clock.GetElapsedTime(stamp.CapturedTimestamp).TotalMilliseconds > 150);
        Assert.Null(System.Windows.Application.Current);
        native.ReleaseInput();
    }

    [Theory]
    [InlineData("request-deadline")]
    [InlineData("parent-deadline")]
    [InlineData("reject-down")]
    [InlineData("reject-up")]
    [InlineData("missing-up-receipt")]
    [InlineData("up-exception")]
    [InlineData("up-timeout")]
    [InlineData("scope-release")]
    [InlineData("cancel")]
    [InlineData("task-stop")]
    public async Task NativeCameraResetOverrunNeverDowngradesNativeOrBusinessFailure(string fault)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        using var owner = TaskExecutionScope.BeginOwned();
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 20, program)
        {
            ExternalClockAdvance = true,
            OnExclusiveEnd = fault == "scope-release" ? () => throw new IOException("scope release failed") : null
        };
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new PitchSceneDevice(clock)
        {
            ResetReleaseDelayMilliseconds = 266,
            RejectResetDown = fault == "reject-down", RejectResetUp = fault == "reject-up",
            OmitResetUpReceipt = fault == "missing-up-receipt",
            AfterResetUp = () =>
            {
                if (fault == "up-exception") throw new IOException("reset up post-call fault");
                if (fault == "up-timeout") throw new TimeoutException("not the local post-completion check");
                if (fault == "cancel") cancellation.Cancel();
                if (fault == "task-stop") TaskExecutionScope.StopUnconfirmedCombat("existing terminal fault");
            }
        };
        var stamp = new CaptureFrameSource(clock).Next();
        var native = new NativeCombatBattleHostIo(flow, device, targetObservation: () =>
            new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 960, 540)
                { Control = new(MotionStatus.Unknown, false) });
        using var parent = fault == "parent-deadline"
            ? UiOperation.Begin("business-parent", TimeSpan.FromMilliseconds(120), clock: clock) : null;
        var request = new CombatBattleHostInput(CombatBattleHostInputKind.CameraReset)
        {
            RequestId = Guid.NewGuid(), Source = stamp,
            DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency * (fault == "request-deadline" ? 120 : 5000) / 1000
        };
        CombatBattleHostInputResult? receipt = null;
        var error = await Record.ExceptionAsync(async () => receipt = await native.SendAsync(request, cancellation.Token));

        Assert.Equal(2, device.ResetAttempts);
        if (fault != "reject-up") Assert.False(device.ResetHeld);
        if (fault is "cancel" or "task-stop")
        {
            Assert.NotNull(error);
            if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.IsType<CombatNotFinishedException>(error);
        }
        else
        {
            Assert.Null(error);
            Assert.NotNull(receipt);
            Assert.Null(receipt.Value.PerformanceRecheck);
            Assert.NotNull(receipt.Value.Error);
        }
        Assert.Null(System.Windows.Application.Current);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CameraResetRecheckRejectsIncompleteProofAndKeepsTheShorterDeadline(bool incomplete)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 attack(.1)");
        using var physical = new PhysicalReplay(clock, false, 20, program) { ExternalClockAdvance = true };
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new PitchSceneDevice(clock) { ResetReleaseDelayMilliseconds = 266 };
        var source = new CaptureFrameSource(clock);
        var native = new NativeCombatBattleHostIo(flow, device, targetObservation: () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            return new(source.Next(), flow.Context.BattleId, CombatObservationQuality.Available, null, 960, 540)
                { Control = new(MotionStatus.Unknown, false) };
        });
        var io = new TracedPitchIo(native, [], (input, receipt) =>
        {
            if (input.Kind != CombatBattleHostInputKind.CameraReset) return receipt;
            Assert.NotNull(receipt.PerformanceRecheck);
            return incomplete ? receipt with { NativeRequested = 1, NativeSubmitted = 1 }
                : receipt with { PerformanceRecheck = receipt.PerformanceRecheck.Value with
                    { DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency / 4 } };
        });
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, TimeoutSeconds = 100 });
        var state = CombatBattleHostResult.Continue;
        for (var i = 0; i < 3000 && device.ResetCount == 0 && state == CombatBattleHostResult.Continue; i++)
            state = await host.AdvanceAsync(flow, default);
        var reset = Assert.Single(io.Receipts.Where(receipt => receipt.Input.Kind == CombatBattleHostInputKind.CameraReset));
        Assert.Equal(1, device.ResetCount);
        if (incomplete)
        {
            Assert.Equal(CombatBattleHostResult.Unconfirmed, state);
            Assert.Equal("host-input-invalid-performance-recheck", host.Reason);
        }
        else
        {
            Assert.Equal(CombatBattleHostResult.Continue, state);
            var deadline = reset.Result.PerformanceRecheck!.Value.DeadlineTimestamp;
            Assert.True(deadline < reset.Input.DeadlineTimestamp);
            clock.Advance(clock.GetElapsedTime(clock.GetTimestamp(), deadline));
            Assert.Equal(CombatBattleHostResult.Unconfirmed, await host.AdvanceAsync(flow, default));
            Assert.Equal("host-input-performance-recheck-deadline", host.Reason);
        }
        Assert.Equal(2, device.ResetAttempts);
        Assert.False(device.ResetHeld);
        Assert.Null(System.Windows.Application.Current);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(110, false)]
    [InlineData(151, false)]
    public async Task NativeCameraResetReservesFortyMillisecondHoldAndReleaseWithinOriginalWindow(int preparation, bool sent)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 20, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new PitchSceneDevice(clock) { PreparationDelayMilliseconds = preparation };
        var stamp = new CaptureFrameSource(clock).Next();
        var native = new NativeCombatBattleHostIo(flow, device, targetObservation: () =>
            new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 960, 540)
                { Control = new(MotionStatus.Unknown, false) });
        var result = await native.SendAsync(new(CombatBattleHostInputKind.CameraReset)
            { RequestId = Guid.NewGuid(), Source = stamp, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency }, default);
        Assert.Equal(sent ? CombatBattleHostInputStatus.Sent : CombatBattleHostInputStatus.NotSent, result.Status);
        Assert.False(device.ResetHeld);
        if (sent)
        {
            Assert.Equal(new[] { 40 }, device.Delays);
            Assert.Equal(2, result.NativeSubmitted);
            Assert.InRange(clock.GetElapsedTime(stamp.CapturedTimestamp).TotalMilliseconds, 30, 150);
        }
        else Assert.Empty(device.Delays);
        native.ReleaseInput();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeCameraResetAlwaysReleasesOnCancellationAndUnknownReceiptNeverReplays(bool cancel)
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 20, program) { ExternalClockAdvance = true };
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new PitchSceneDevice(clock) { RejectResetDown = !cancel,
            AfterResetDown = () => { if (cancel) cancellation.Cancel(); } };
        var source = new CaptureFrameSource(clock);
        var native = new NativeCombatBattleHostIo(flow, device, targetObservation: () =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            return new(source.Next(), flow.Context.BattleId, CombatObservationQuality.Available, null, 960, 540)
                { Control = new(MotionStatus.Unknown, false) };
        });
        var traces = new List<CombatBattleHostTrace>();
        using var host = new CombatBattleHost(new TracedPitchIo(native, traces), new()
            { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        var error = await Record.ExceptionAsync(async () =>
        {
            for (var i = 0; i < 3000 && result == CombatBattleHostResult.Continue; i++)
                result = await host.AdvanceAsync(flow, cancellation.Token);
        });
        Assert.False(device.ResetHeld);
        Assert.Equal(2, device.ResetAttempts);
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else
        {
            Assert.Null(error);
            Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
            Assert.Contains("host-input-outcome-unknown", host.Reason);
            for (var i = 0; i < 3; i++) Assert.Equal(result, await host.AdvanceAsync(flow, default));
            Assert.Equal(2, device.ResetAttempts);
        }
    }

    private sealed class TracedPitchIo(NativeCombatBattleHostIo native, List<CombatBattleHostTrace> traces,
        Func<CombatBattleHostInput, CombatBattleHostInputResult, CombatBattleHostInputResult>? transform = null) : ICombatBattleHostIo
    {
        public TimeProvider Clock => native.Clock;
        public Guid BattleId => native.BattleId;
        public CombatBattleObservation ObserveTarget() => native.ObserveTarget();
        public PartySetupFinishObservation ObservePartyBar() => native.ObservePartyBar();
        public List<(CombatBattleHostInput Input, CombatBattleHostInputResult Result)> Receipts { get; } = [];
        public async ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct)
        {
            var result = await native.SendAsync(input, ct);
            result = transform?.Invoke(input, result) ?? result;
            Receipts.Add((input, result));
            return result;
        }
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => native.DelayAsync(milliseconds, ct);
        public void ReleaseInput() => native.ReleaseInput();
        public void Trace(CombatBattleHostTrace trace) => traces.Add(trace);
    }

    private sealed class PitchSceneDevice(FakeTimeProvider clock) : ICombatHostInputDevice
    {
        public TimeProvider Clock => clock;
        public ILogger Logger => NullLogger.Instance;
        public bool ResetEffective { get; init; } = true;
        public bool RejectResetDown { get; init; }
        public bool RejectResetUp { get; init; }
        public bool OmitResetUpReceipt { get; init; }
        public Action? AfterResetDown { get; init; }
        public Action? AfterResetUp { get; init; }
        public int PreparationDelayMilliseconds { get; init; }
        public int ResetReleaseDelayMilliseconds { get; init; }
        public int ResetAttempts { get; private set; }
        public List<int> Delays { get; } = [];
        public double PitchDegrees { get; private set; } = 85;
        public double MinimumPitchDegrees { get; private set; } = 85;
        public bool PartyOpen { get; private set; }
        public Func<bool> CanOpenParty { get; set; } = () => false;
        public bool ResetHeld { get; private set; }
        public int ResetCount { get; private set; }
        public InputDispatchCapture BeginNativeCapture(Action guard) => new(guard);
        public void PrepareInput() => clock.Advance(TimeSpan.FromMilliseconds(PreparationDelayMilliseconds));
        private static void Receipt() => new WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0)
            .DispatchInput(new User32.INPUT[1]);
        public void MoveCamera(int x, int y)
        {
            Receipt();
            PitchDegrees = Math.Clamp(PitchDegrees + y * .01, -89, 89);
            MinimumPitchDegrees = Math.Min(MinimumPitchDegrees, PitchDegrees);
        }
        public void SetCameraReset(bool down)
        {
            ResetAttempts++;
            if (down && RejectResetDown || !down && RejectResetUp)
                new WindowsInputMessageDispatcher(null, _ => 0, () => 5).DispatchInput(new User32.INPUT[1]);
            else if (down || !OmitResetUpReceipt) Receipt();
            ResetHeld = down;
            if (down) { ResetCount++; AfterResetDown?.Invoke(); }
            else
            {
                clock.Advance(TimeSpan.FromMilliseconds(ResetReleaseDelayMilliseconds));
                if (ResetEffective) PitchDegrees = 0;
                AfterResetUp?.Invoke();
            }
        }
        public void MoveForward(bool down) => Receipt();
        public void PressDrop() { Receipt(); PartyOpen = false; }
        public void PressParty() { Receipt(); PartyOpen = !PartyOpen && CanOpenParty(); }
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Delays.Add(milliseconds);
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return ValueTask.CompletedTask;
        }
    }
}
