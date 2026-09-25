using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task S58SlowReleaseWaitsForNewEvidenceInsteadOfTerminatingBattle(bool selection)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram(selection ? "琴 e(required)" : "那维莱特 wait(.1)");
        using var physical = new PhysicalReplay(clock, false, 50, program)
            { SelectionNeedsMovement = selection, ExternalClockAdvance = true };
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new HostDevice(clock) { NativeReceipts = true, DownDelayMs = .1723,
            HoldOverheadMs = .2538, UpDelayMs = 57.6344, PreparationDelayMs = .9145 };
        var native = new NativeCombatBattleHostIo(flow, device);
        var scene = new LifecycleScene(clock, flow.Context.BattleId, physical.Producer, native.SendAsync);
        using var host = new CombatBattleHost(scene, new() { FinishDetectionEnabled = false });
        if (!selection)
        {
            await flow.RunRoundAsync(default);
            await host.AdvanceAsync(flow, default);
            await flow.RunRoundAsync(default);
            clock.Advance(TimeSpan.FromSeconds(46));
        }
        for (var i = 0; i < 100 && scene.Receipts.Count == 0; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));

        var request = Assert.Single(scene.Requests);
        Assert.Equal(selection, request.SelectionGoal != null);
        Assert.Equal(new[] { "forward-down", "forward-up" }, device.Inputs);
        Assert.Equal(2, Assert.Single(scene.Receipts).NativeSubmitted);
        Assert.Null(scene.Receipts[0].Error);
        Assert.NotNull(scene.Receipts[0].PerformanceRecheck);
        Assert.InRange(scene.InputMilliseconds[0], 158.974, 158.976);
        var revision = flow.Context.InputAttemptRevision;
        scene.Frozen = scene.Last;
        for (var i = 0; i < 3; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Single(scene.Requests);
        Assert.Equal(revision, flow.Context.InputAttemptRevision);

        scene.Frozen = null;
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Single(scene.Requests);
        Assert.Equal("host-input-performance-rechecked", host.Reason);
        if (selection)
        {
            physical.SetFrontActor("琴");
            for (var i = 0; i < 80 && physical.Inputs.Count == 0; i++)
                Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
            Assert.Equal("琴", Assert.Single(physical.Inputs).Actor);
            Assert.Single(scene.Requests);
        }
    }

    [Theory]
    [InlineData("request-deadline")]
    [InlineData("parent-deadline")]
    [InlineData("reject-down")]
    [InlineData("reject-up")]
    [InlineData("up-exception")]
    [InlineData("up-timeout")]
    [InlineData("scope-release")]
    [InlineData("no-receipts")]
    [InlineData("stale-before-input")]
    [InlineData("cancel")]
    [InlineData("task-stop")]
    public async Task S58ProducerCannotDowngradeBusinessOrNativeFailures(string fault)
    {
        using var owner = TaskExecutionScope.BeginOwned();
        using var cancellation = new CancellationTokenSource();
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e(required)"))
            { OnExclusiveEnd = fault == "scope-release" ? () => throw new IOException("scope release failed") : null };
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e(required)"), physical);
        var device = new HostDevice(clock)
        {
            NativeReceipts = fault != "no-receipts", UpDelayMs = 58,
            PreparationDelayMs = fault == "stale-before-input" ? 151 : 0,
            RejectForward = down => fault == (down ? "reject-down" : "reject-up"),
            AfterForward = down =>
            {
                if (down) return;
                if (fault == "up-exception") throw new IOException("up post-call fault");
                if (fault == "up-timeout") throw new TimeoutException("not the local post-completion check");
                if (fault == "cancel") cancellation.Cancel();
                if (fault == "task-stop") TaskExecutionScope.StopUnconfirmedCombat("existing terminal fault");
            }
        };
        var native = new NativeCombatBattleHostIo(flow, device);
        using var parent = fault == "parent-deadline"
            ? UiOperation.Begin("business-parent", TimeSpan.FromMilliseconds(120), clock: clock) : null;
        var request = new CombatBattleHostInput(CombatBattleHostInputKind.Approach)
        {
            RequestId = Guid.NewGuid(), Source = new CaptureFrameSource(clock).Next(),
            DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency * (fault == "request-deadline" ? 120 : 1000) / 1000
        };
        CombatBattleHostInputResult? receipt = null;
        var error = await Record.ExceptionAsync(async () => receipt = await native.SendAsync(request, cancellation.Token));
        if (fault is "cancel" or "task-stop")
        {
            Assert.NotNull(error);
            if (fault == "cancel") Assert.IsAssignableFrom<OperationCanceledException>(error);
            else Assert.IsType<CombatNotFinishedException>(error);
        }
        else
        {
            Assert.Null(error);
            Assert.Null(receipt!.Value.PerformanceRecheck);
            Assert.NotNull(receipt.Value.Error);
        }
        if (fault == "stale-before-input") Assert.Empty(device.Inputs);
        else Assert.Equal(new[] { "forward-down", "forward-up" }, device.Inputs);
    }

    [Theory]
    [InlineData("pre-input-control")]
    [InlineData("foreign-session")]
    [InlineData("foreign-battle")]
    [InlineData("unavailable")]
    [InlineData("late")]
    [InlineData("stale")]
    public async Task S58RecheckNeverAdvancesStrategyOrInputsUsingInvalidEvidence(string fault)
    {
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e(required)")) { SelectionNeedsMovement = true, ExternalClockAdvance = true };
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e(required)"), physical);
        var device = new HostDevice(clock) { NativeReceipts = true, UpDelayMs = 58 };
        var native = new NativeCombatBattleHostIo(flow, device);
        var scene = new LifecycleScene(clock, flow.Context.BattleId, physical.Producer, native.SendAsync);
        using var host = new CombatBattleHost(scene, new() { FinishDetectionEnabled = false });
        for (var i = 0; i < 100 && scene.Receipts.Count == 0; i++) await host.AdvanceAsync(flow, default);
        var receipt = Assert.Single(scene.Receipts);
        Assert.NotNull(receipt.PerformanceRecheck);
        var invalid = scene.Last with { Source = physical.Producer.Next() };
        invalid = fault switch
        {
            "pre-input-control" => invalid with { Source = physical.Producer.Next(receipt.CompletedTimestamp!.Value - clock.TimestampFrequency / 100), Control = new(MotionStatus.Unknown, true) },
            "foreign-session" => invalid with { Source = new CaptureFrameSource(clock).Next() },
            "foreign-battle" => invalid with { BattleId = Guid.NewGuid() },
            "unavailable" => invalid with { Quality = CombatObservationQuality.Unavailable },
            "late" => invalid with { Quality = CombatObservationQuality.Late },
            _ => invalid with { Source = physical.Producer.Next(clock.GetTimestamp() - clock.TimestampFrequency) }
        };
        scene.Frozen = invalid;
        var revision = flow.Context.InputAttemptRevision;
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal(revision, flow.Context.InputAttemptRevision);
        Assert.Single(scene.Requests);
        Assert.Equal("host-input-performance-awaiting-fresh-evidence", host.Reason);
    }

    [Theory]
    [InlineData("deadline-before-read")]
    [InlineData("deadline-during-read")]
    [InlineData("cancel")]
    public async Task S58PendingRecheckKeepsItsOriginalDeadlineAndCancellation(string ending)
    {
        var clock = new FakeTimeProvider();
        using var cancellation = new CancellationTokenSource();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e(required)")) { SelectionNeedsMovement = true, ExternalClockAdvance = true };
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e(required)"), physical);
        var device = new HostDevice(clock) { NativeReceipts = true, UpDelayMs = 58 };
        var native = new NativeCombatBattleHostIo(flow, device);
        var scene = new LifecycleScene(clock, flow.Context.BattleId, physical.Producer, native.SendAsync);
        using var host = new CombatBattleHost(scene, new() { FinishDetectionEnabled = false });
        for (var i = 0; i < 100 && scene.Receipts.Count == 0; i++) await host.AdvanceAsync(flow, default);
        var receipt = Assert.Single(scene.Receipts);
        var deadline = receipt.PerformanceRecheck!.Value.DeadlineTimestamp;
        Assert.True(deadline <= scene.Requests[0].DeadlineTimestamp);
        var revision = flow.Context.InputAttemptRevision;
        if (ending == "cancel")
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.AdvanceAsync(flow, cancellation.Token));
        }
        else
        {
            var remaining = clock.GetElapsedTime(clock.GetTimestamp(), deadline);
            clock.Advance(remaining - (ending == "deadline-during-read" ? TimeSpan.FromMilliseconds(1) : TimeSpan.Zero));
            scene.ReadDelayMs = 5;
            Assert.Equal(CombatBattleHostResult.Unconfirmed, await host.AdvanceAsync(flow, default));
            Assert.Equal("host-input-performance-recheck-deadline", host.Reason);
        }
        Assert.Single(scene.Requests);
        Assert.Equal(revision, flow.Context.InputAttemptRevision);
        Assert.Equal(new[] { "forward-down", "forward-up" }, device.Inputs);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("goal")]
    [InlineData("counts")]
    [InlineData("deadline")]
    [InlineData("error")]
    public async Task S58HostRejectsMismatchedRecheckEvidence(string mismatch)
    {
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e(required)")) { SelectionNeedsMovement = true, ExternalClockAdvance = true };
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e(required)"), physical);
        var device = new HostDevice(clock) { NativeReceipts = true, UpDelayMs = 58 };
        var native = new NativeCombatBattleHostIo(flow, device);
        async ValueTask<CombatBattleHostInputResult> Send(CombatBattleHostInput request, CancellationToken ct)
        {
            var result = await native.SendAsync(request, ct);
            var proof = result.PerformanceRecheck!.Value;
            return mismatch switch
            {
                "request" => result with { PerformanceRecheck = proof with { RequestId = Guid.NewGuid() } },
                "goal" => result with { PerformanceRecheck = proof with { SelectionGoal = Guid.NewGuid() } },
                "counts" => result with { NativeSubmitted = 1 },
                "deadline" => result with { PerformanceRecheck = proof with { DeadlineTimestamp = request.DeadlineTimestamp + 1 } },
                _ => result with { Error = new IOException("independent failure") }
            };
        }
        var scene = new LifecycleScene(clock, flow.Context.BattleId, physical.Producer, Send);
        using var host = new CombatBattleHost(scene, new() { FinishDetectionEnabled = false });
        CombatBattleHostResult state = default;
        for (var i = 0; i < 100 && scene.Receipts.Count == 0; i++) state = await host.AdvanceAsync(flow, default);
        Assert.Single(scene.Requests);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, state);
        Assert.Equal("host-input-invalid-performance-recheck", host.Reason);
        Assert.Empty(physical.Inputs);
    }

    private sealed class LifecycleScene(FakeTimeProvider clock, Guid battle,
        CaptureFrameSource source,
        Func<CombatBattleHostInput, CancellationToken, ValueTask<CombatBattleHostInputResult>> send) : ICombatBattleHostIo
    {
        public TimeProvider Clock => clock;
        public Guid BattleId => battle;
        internal CombatBattleObservation? Frozen;
        internal CombatBattleObservation Last;
        internal readonly List<CombatBattleHostInput> Requests = [];
        internal readonly List<CombatBattleHostInputResult> Receipts = [];
        internal readonly List<double> InputMilliseconds = [];
        internal int ReadDelayMs = 1;
        public CombatBattleObservation ObserveTarget()
        {
            clock.Advance(TimeSpan.FromMilliseconds(ReadDelayMs));
            return Last = Frozen ?? new CombatBattleObservation(source.Next(), battle, CombatObservationQuality.Available,
                new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                    new(910, 400, 100, 9, 850), 1, SeekCueKind.HealthBar), 1920, 1080)
                { Control = new(MotionStatus.Unknown, false) };
        }
        public PartySetupFinishObservation ObservePartyBar() => throw new InvalidOperationException("No finish probe expected");
        public async ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct)
        {
            Requests.Add(input);
            var started = clock.GetTimestamp();
            var result = await send(input, ct);
            InputMilliseconds.Add(clock.GetElapsedTime(started).TotalMilliseconds);
            Receipts.Add(result);
            return result;
        }
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); return ValueTask.CompletedTask; }
        public void ReleaseInput() { }
    }
}
