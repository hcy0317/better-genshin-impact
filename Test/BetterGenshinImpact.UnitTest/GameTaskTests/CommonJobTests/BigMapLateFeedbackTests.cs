using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Xunit;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class BigMapLateFeedbackTests(ITestOutputHelper output)
{
    [Fact]
    public async Task LateMapCompletesTheOriginalRequestWithoutNetworkReplayOrASecondM()
    {
        using var owner = TaskExecutionScope.BeginOwned();
        TaskExecutionScope.MarkUnsafeNetworkReplay("earlier script side effect; no replay allowed");
        var replay = new Replay("late-map");
        bool? completed = null;
        var error = await Record.ExceptionAsync(async () => completed = await replay.Run());
        output.WriteLine($"protocol={typeof(BigMapOpenAttempt).Assembly.GetName().Name} mvid={typeof(BigMapOpenAttempt).Assembly.ManifestModule.ModuleVersionId} M={replay.Inputs} passive={replay.PassiveFrames} native={replay.NativeCalls}");
        Assert.Null(error);
        Assert.True(completed);
        Assert.Equal(1, replay.Inputs);
        Assert.Equal(0, replay.NativeCalls);
        Assert.DoesNotContain("stable-candidate-verify-before-retry", replay.States);
        Assert.Null(TaskExecutionScope.Failure);
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
    }

    [Theory]
    [InlineData("hud")]
    [InlineData("single-map")]
    public async Task HudOrOnlyOneMapDoesNotClearNoFeedback(string mode)
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var replay = new Replay(mode);
        await Assert.ThrowsAsync<TaskFailureRecoveryException>(() => replay.Run());
        Assert.Equal(1, replay.Inputs);
        Assert.Equal(0, replay.NativeCalls);
    }

    [Theory]
    [InlineData("foreign")]
    [InlineData("duplicate")]
    [InlineData("stale")]
    [InlineData("before-fence")]
    [InlineData("wrong-target")]
    [InlineData("changed-target-at-complete")]
    public async Task LateMapWithoutTheOriginalRequestProofRemainsFailure(string mode)
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var replay = new Replay(mode);
        await Assert.ThrowsAsync<TaskFailureRecoveryException>(() => replay.Run());
        Assert.Equal(1, replay.Inputs);
        Assert.Equal(0, replay.NativeCalls);
    }

    [Fact]
    public async Task AClosedOriginalParentCannotCompleteTheLateGoal()
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var replay = new Replay("late-map") { BeforePassive = owner.Dispose };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => replay.Run());
        Assert.Equal(1, replay.Inputs);
    }

    [Fact]
    public async Task AnExpiredOriginalUiParentCannotCompleteTheLateGoal()
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var replay = new Replay("late-map");
        using var parent = UiOperation.Begin("original-map-parent", TimeSpan.FromSeconds(1), clock: replay.Clock);
        Assert.NotNull(await Record.ExceptionAsync(() => replay.Run()));
        Assert.Equal(1, replay.Inputs);
    }

    [Fact]
    public async Task ACompletedUiChildCannotBeRevivedByLateMap()
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var replay = new Replay("late-map");
        using var child = UiOperation.Begin("original-map-child", TimeSpan.FromSeconds(30), clock: replay.Clock);
        replay.BeforePassive = child.Dispose;
        Assert.NotNull(await Record.ExceptionAsync(() => replay.Run()));
        Assert.Equal(1, replay.Inputs);
    }

    [Fact]
    public async Task TheExistingNetworkBoolStillRequiresARealInterruption()
    {
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock); var frames = 0;
        var recovered = await NetworkRecovery.WaitAsync(false,
            () => new UiSnapshot(++frames) { BigMap = true }.WithSource(source.Next(), clock, UiSnapshot.RecoveryMaximumAge),
            () => { }, () => true, (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock);
        Assert.False(recovered);
    }

    [Fact]
    public async Task ConfirmedNetworkKeepsItsExistingStableRecoveryBool()
    {
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock); var frames = 0;
        var recovered = await NetworkRecovery.WaitAsync(true,
            () => new UiSnapshot(++frames) { MainHud = true }.WithSource(source.Next(), clock, UiSnapshot.RecoveryMaximumAge),
            () => { }, () => true, (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock);
        Assert.True(recovered);
    }

#if !MAP_LATE_OLD
    [Theory]
    [InlineData("callback-overrun")]
    [InlineData("trace-overrun")]
    [InlineData("final-overrun")]
    [InlineData("target-check-overrun")]
    public async Task TheOriginalPassiveDeadlineCannotBeCrossedAtAnyGoalAcceptanceBoundary(string stage)
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var replay = new Replay(stage);
        await Assert.ThrowsAsync<TaskFailureRecoveryException>(() => replay.Run());
        Assert.Equal(1, replay.Inputs);
        Assert.Equal(0, replay.NativeCalls);
    }

    [Theory]
    [InlineData("callback-within")]
    [InlineData("trace-within")]
    [InlineData("final-within")]
    public async Task AGoalStillInsideTheSamePassiveDeadlineCanComplete(string stage)
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var replay = new Replay(stage);
        Assert.True(await replay.Run());
        Assert.Equal(1, replay.Inputs);
        Assert.Equal(0, replay.NativeCalls);
    }
#endif

    private sealed class Replay(string mode)
    {
        internal readonly FakeTimeProvider Clock = new();
        private CaptureFrameSource? _source;
        internal int Inputs, PassiveFrames, NativeCalls;
        internal List<string> States { get; } = [];
        internal Action? BeforePassive;
        private bool _targetCurrent = true;
        private CaptureFrameStamp? _firstPassive;
        private long _inputCompleted;
        private long _passiveStarted;
        private bool _inPassive, _goalReady;
        private bool DeadlineCase => mode.EndsWith("-overrun") || mode.EndsWith("-within");
        private void GoalCost(string stage)
        {
            if (mode == stage + "-overrun") Clock.Advance(TimeSpan.FromMilliseconds(900));
            if (mode == stage + "-within") Clock.Advance(TimeSpan.FromMilliseconds(400));
        }
#if !MAP_LATE_OLD
        private BigMapGoalFeedback? _feedback;
#endif
        internal async Task<bool> Run()
        {
            _source = new CaptureFrameSource(Clock);
#if !MAP_LATE_OLD
            _feedback = new BigMapGoalFeedback(Clock, () =>
            { if (_goalReady && mode == "target-check-overrun") Clock.Advance(TimeSpan.FromMilliseconds(900)); return _targetCurrent; });
#endif
            using var capture = new InputDispatchCapture();
            var outcome = await BigMapOpenAttempt.RunAsync(() =>
                new ImageRegion(new Mat(16, 16, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = _source.Next() },
                _ => false, _ => null, () => { }, () => InputDispatchCapture.DispatchTransport(admit =>
                { admit(); Inputs++; Clock.Advance(TimeSpan.FromMilliseconds(60)); _inputCompleted = Clock.GetTimestamp(); }), Delay, default,
                TimeSpan.FromMilliseconds(500), Clock, () => "fake transport; native0"
#if !MAP_LATE_OLD
                , inputFence: _feedback.Arm
#endif
            );
            Assert.Equal(BigMapOpenOutcome.NoVisualFeedback, outcome);
            try
            {
#if !MAP_LATE_OLD
                return await BigMapOpenAttempt.CompleteWithGoalAsync(outcome, null, (_, ct) =>
                {
                    _inPassive = true; _passiveStarted = Clock.GetTimestamp();
                    return NetworkRecovery.WaitOutcomeAsync(false, Capture, () => { }, () => true, Delay, ct, Clock,
                        (state, _) => { States.Add(state); if (state == "goal-complete-no-replay") GoalCost("trace"); }, GoalObserved);
                }, default, GoalStillCurrent);
#else
                return await BigMapOpenAttempt.CompleteAsync(outcome, null, (_, ct) =>
                    NetworkRecovery.WaitAsync(false, Capture, () => { }, () => true, Delay, ct, Clock,
                        (state, _) => States.Add(state)), default);
#endif
            }
            finally { NativeCalls = capture.NativeCalls; }
        }
#if !MAP_LATE_OLD
        private bool GoalObserved(UiSnapshot snapshot)
        {
            var ready = _feedback!.Observe(snapshot);
            _goalReady = ready;
            if (ready && mode == "changed-target-at-complete") _targetCurrent = false;
            if (ready) GoalCost("callback");
            return ready;
        }
#endif
        #if !MAP_LATE_OLD
        private bool GoalStillCurrent()
        { var current = _feedback!.IsCurrent(); if (current) GoalCost("final"); return current; }
        #endif
        private UiSnapshot Capture()
        {
            PassiveFrames++;
            BeforePassive?.Invoke();
            var map = mode != "hud" && (mode != "single-map" || PassiveFrames == 1);
            if (DeadlineCase) map = Clock.GetElapsedTime(_passiveStarted).TotalSeconds >= 178.5;
            if (mode == "wrong-target") _targetCurrent = false;
            var stamp = _source!.Next();
            if (mode == "foreign") stamp = stamp with { SessionId = Guid.NewGuid() };
            if (mode == "duplicate") { _firstPassive ??= stamp; stamp = _firstPassive.Value; }
            if (mode == "before-fence") stamp = stamp with { CapturedTimestamp = _inputCompleted };
            if (mode == "stale") Clock.Advance(TimeSpan.FromSeconds(3));
            return new UiSnapshot(PassiveFrames) { BigMap = map, MainHud = !map }
                .WithSource(stamp, Clock, UiSnapshot.RecoveryMaximumAge);
        }
        private Task Delay(int ms, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(_inPassive && DeadlineCase && PassiveFrames == 1 ? 1500 : ms)); return Task.CompletedTask; }
    }
}
