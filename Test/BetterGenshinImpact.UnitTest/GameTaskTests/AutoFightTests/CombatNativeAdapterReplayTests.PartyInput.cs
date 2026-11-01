using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using BetterGenshinImpact.Core.Simulator.Extensions;
using Fischless.WindowsInput;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Fact]
    public void UiDeadlineOwnershipCannotBeCopiedFromItsMessage()
    {
        var clock = new FakeTimeProvider();
        using var operation = UiOperation.Begin("local-party-deadline", TimeSpan.FromMilliseconds(10), default, clock: clock);
        clock.Advance(TimeSpan.FromMilliseconds(11));
        var actual = Assert.Throws<TimeoutException>(operation.Check);
        Assert.True(operation.IsOwnDeadlineFailure(actual));
        Assert.False(operation.IsOwnDeadlineFailure(new TimeoutException(actual.Message)));
        Assert.False(operation.IsOwnDeadlineFailure(null!));
    }

    [Theory]
    [InlineData("internal")]
    [InlineData("release")]
    [InlineData("parent")]
    [InlineData("cancel")]
    public async Task AReleasedPartyPulseCannotUseOtherFailuresAsItsLocalDeadline(string failureKind)
    {
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var cancellation = new CancellationTokenSource();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new PartyPulseDevice(clock)
        {
            ExtraHoldMilliseconds = 100,
            HoldFailure = failureKind == "internal" ? new TimeoutException("Internal hold failure is not a UI budget receipt.") : null,
            ReleaseFailure = failureKind == "release" ? new IOException("Native release cleanup failed.") : null,
            AfterHold = () => { if (failureKind == "cancel") cancellation.Cancel(); }
        };
        var native = new NativeCombatBattleHostIo(flow, device, () => throw new InvalidOperationException("No UI observation before input proof."));
        var request = new CombatBattleHostInput(CombatBattleHostInputKind.OpenParty)
        { RequestId = Guid.NewGuid(), Source = source.Next(), DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency * 2 };
        CombatBattleHostInputResult? receipt = null;
        using var parent = failureKind == "parent"
            ? UiOperation.Begin("short-parent", TimeSpan.FromMilliseconds(120), default, clock: clock) : null;
        var error = await Record.ExceptionAsync(async () => receipt = await native.SendAsync(request, cancellation.Token));
        Assert.True(error != null || receipt?.Error != null);
        Assert.Null(receipt?.PerformanceRecheck);
        Assert.False(device.Held); Assert.Equal(1, device.PartyCalls);
        native.ReleaseInput(); Assert.Equal(1, device.PartyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARealPartyPulseThatReleasesAfterItsOwnLocalCheckTimeoutStillNeedsPostInputProof(bool close)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var device = new PartyPulseDevice(clock) { ExtraHoldMilliseconds = close ? 0 : 100 };
        var native = new NativeCombatBattleHostIo(flow, device, () =>
        {
            var stamp = source.Next();
            return new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, true, (ulong)stamp.Sequence) { Source = stamp };
        });
        CombatBattleHostInput Request(CombatBattleHostInputKind kind, CaptureFrameStamp stamp) => new(kind, PartyEvidence: true)
        { RequestId = Guid.NewGuid(), Source = stamp, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency * 2 };
        var observed = source.Next();
        if (close)
        {
            Assert.Null((await native.SendAsync(Request(CombatBattleHostInputKind.OpenParty, observed), default)).Error);
            clock.Advance(TimeSpan.FromMilliseconds(50)); observed = native.ObservePartyBar().Source;
            device.ExtraHoldMilliseconds = 100;
        }
        var request = Request(close ? CombatBattleHostInputKind.CloseParty : CombatBattleHostInputKind.OpenParty, observed);
        var result = await native.SendAsync(request, default);
        Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
        Assert.Equal(close ? 4 : 2, result.NativeRequested); Assert.Equal(result.NativeRequested, result.NativeSubmitted);
        Assert.False(device.Held); Assert.False(result.NativeUncertain); Assert.Null(result.Error);
        Assert.NotNull(result.PerformanceRecheck); Assert.Equal(request.DeadlineTimestamp, result.PerformanceRecheck.Value.DeadlineTimestamp);
        Assert.Equal(close ? 2 : 1, device.PartyCalls); var count = device.PartyCalls;
        native.ReleaseInput(); Assert.Equal(count, device.PartyCalls);
    }

    private sealed class PartyPulseDevice(FakeTimeProvider clock) : ICombatHostInputDevice
    {
        public TimeProvider Clock => clock;
        public ILogger Logger => NullLogger.Instance;
        public int ExtraHoldMilliseconds { get; set; }
        public Func<int, int>? ExtraHoldForCall { get; init; }
        public Action<int>? OnPartyDown { get; init; }
        public Action? AfterHold { get; init; }
        public Exception? HoldFailure { get; init; }
        public Exception? ReleaseFailure { get; init; }
        public List<string> Inputs { get; } = [];
        public int PartyCalls { get; private set; }
        public bool Held { get; private set; }
        public void PrepareInput() { }
        public InputDispatchCapture BeginNativeCapture(Action guard) => new(guard);
        private static void Receipt(int count) => new WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0)
            .DispatchInput(new User32.INPUT[count]);
        public void PressParty()
        {
            PartyCalls++; Inputs.Add("party");
            InputSimulatorExtension.PulseCore(() => { Receipt(1); Held = true; OnPartyDown?.Invoke(PartyCalls); },
                () => { Receipt(1); Held = false; if (ReleaseFailure != null) throw ReleaseFailure; },
                milliseconds =>
                {
                    clock.Advance(TimeSpan.FromMilliseconds(milliseconds + ExtraHoldMilliseconds + (ExtraHoldForCall?.Invoke(PartyCalls) ?? 0)));
                    AfterHold?.Invoke();
                    if (HoldFailure != null) throw HoldFailure;
                },
                () => UiOperation.Current?.Check());
        }
        public void PressDrop() { Inputs.Add("drop"); Receipt(2); }
        public void MoveCamera(int x, int y) => throw new InvalidOperationException("Party input replay cannot move the camera.");
        public void MoveForward(bool down) => throw new InvalidOperationException("Party input replay cannot move the avatar.");
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(milliseconds)); return ValueTask.CompletedTask; }
    }

    [Theory]
    [InlineData(false, "fresh", 0, true)]
    [InlineData(true, "fresh", 0, true)]
    [InlineData(true, "missing", 0, false)]
    [InlineData(true, "repeated", 0, false)]
    [InlineData(true, "foreign", 0, false)]
    [InlineData(true, "frequency", 0, false)]
    [InlineData(false, "duplicate-bar", 0, false)]
    [InlineData(true, "fresh", 280, true)]
    [InlineData(true, "fresh", 270, false)]
    public async Task LateCompletePartyInputStillNeedsExistingPartyAndPostCloseWorldProof(bool lateClose, string feedback,
        int parentMilliseconds, bool completed)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 attack(.1)");
        using var physical = new PhysicalReplay(clock, false, 50, program) { ExternalClockAdvance = true };
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var source = new CaptureFrameSource(clock);
        var partyOpen = false; var partyCalls = 0;
        CaptureFrameStamp? beforeClose = null, firstPartyBar = null;
        var observations = new List<CombatBattleObservation>();
        var partySamples = new List<PartySetupFinishObservation>();
        var traces = new List<CombatBattleHostTrace>();
        var device = new PartyPulseDevice(clock)
        {
            OnPartyDown = call => { partyCalls = call; partyOpen = call == 1; },
            ExtraHoldForCall = call => call == (lateClose ? 2 : 1) ? 100 : 0
        };
        CombatBattleObservation Observe()
        {
            clock.Advance(TimeSpan.FromMilliseconds(20));
            var stamp = source.Next();
            if (partyCalls < 2) beforeClose = stamp;
            if (partyCalls >= 2) stamp = feedback switch
            {
                "repeated" => beforeClose!.Value,
                "foreign" => stamp with { SessionId = Guid.NewGuid() },
                "frequency" => stamp with { TimestampFrequency = stamp.TimestampFrequency + 1 },
                _ => stamp
            };
            var observation = new CombatBattleObservation(stamp, flow.Context.BattleId,
                partyOpen || !physical.PassiveObservationAvailable || partyCalls >= 2 && feedback == "missing"
                    ? CombatObservationQuality.Unavailable : CombatObservationQuality.Available,
                null, 1920, 1080) { Control = new(MotionStatus.Unknown, false) };
            observations.Add(observation); return observation;
        }
        PartySetupFinishObservation Party()
        {
            clock.Advance(TimeSpan.FromMilliseconds(20)); var stamp = source.Next();
            if (partyOpen && feedback == "duplicate-bar") { firstPartyBar ??= stamp; stamp = firstPartyBar.Value; }
            var sample = new PartySetupFinishObservation(stamp.Sequence, stamp.CapturedAt, 1920, 1080, partyOpen, (ulong)stamp.Sequence)
                { Source = stamp };
            partySamples.Add(sample); return sample;
        }
        var native = new NativeCombatBattleHostIo(flow, device, Party, Observe);
        var io = new TracedPitchIo(native, traces);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1, TimeoutSeconds = 10 });
        var result = CombatBattleHostResult.Continue;
        UiOperation? parent = null;
        try
        {
            for (var i = 0; i < 400 && result == CombatBattleHostResult.Continue; i++)
            {
                if (parent == null && parentMilliseconds > 0 && traces.LastOrDefault()?.State == "CloseParty")
                    parent = UiOperation.Begin("party-tight-parent", TimeSpan.FromMilliseconds(parentMilliseconds), default, clock: clock);
                result = await host.AdvanceAsync(flow, default);
            }
        }
        catch (TimeoutException) when (parentMilliseconds > 0) { result = CombatBattleHostResult.Unconfirmed; }
        finally { parent?.Dispose(); }
        output.WriteLine($"lateClose={lateClose} result={result} reason={host.Reason} partyCalls={partyCalls} t={clock.GetTimestamp()}");
        Assert.Equal(completed ? CombatBattleHostResult.Completed : CombatBattleHostResult.Unconfirmed, result);
        var late = Assert.Single(io.Receipts.Where(value => value.Input.Kind ==
            (lateClose ? CombatBattleHostInputKind.CloseParty : CombatBattleHostInputKind.OpenParty)));
        Assert.NotNull(late.Result.PerformanceRecheck); Assert.Null(late.Result.Error);
        if (feedback != "duplicate-bar")
        {
            Assert.True(partySamples.Count(sample => sample.BarVisible) >= 2);
            Assert.Contains(traces, trace => trace.PartyReason == "confirmed");
            Assert.Equal(2, partyCalls); Assert.Single(device.Inputs.Where(value => value == "drop"));
        }
        Assert.True(partyCalls <= 2);
        Assert.All(io.Receipts.GroupBy(value => value.Input.Kind), group => Assert.Single(group));
        if (!completed) Assert.DoesNotContain(traces, trace => trace.Result == CombatBattleHostResult.Completed);
        if (lateClose && completed)
            Assert.Contains(observations, value => value.Quality == CombatObservationQuality.Available &&
                value.Source.CapturedTimestamp >= late.Result.ObservableAfterTimestamp!.Value + clock.TimestampFrequency * 60 / 1000);
    }

    [Fact]
    public async Task PartyCloseDoesNotBeginItsSecondPulseAfterTheOriginalAdmissionWindow()
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var lateDrop = false;
        var device = new HostDevice(clock) { NativeReceipts = true,
            AfterDrop = () => { if (lateDrop) clock.Advance(TimeSpan.FromMilliseconds(151)); } };
        var source = new CaptureFrameSource(clock);
        var native = new NativeCombatBattleHostIo(flow, device, () =>
        {
            var stamp = source.Next();
            return new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, true, (ulong)stamp.Sequence) { Source = stamp };
        });
        CombatBattleHostInput Request(CombatBattleHostInputKind kind, CaptureFrameStamp stamp) => new(kind, PartyEvidence: true)
        { RequestId = Guid.NewGuid(), Source = stamp, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency * 2 };
        Assert.Null((await native.SendAsync(Request(CombatBattleHostInputKind.OpenParty, source.Next()), default)).Error);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        var page = native.ObservePartyBar(); lateDrop = true;
        var result = await native.SendAsync(Request(CombatBattleHostInputKind.CloseParty, page.Source), default);
        Assert.Equal(new[] { "party", "drop" }, device.Inputs);
        Assert.NotNull(result.Error); Assert.Null(result.PerformanceRecheck);
        native.ReleaseInput();
        Assert.Equal(new[] { "party", "drop" }, device.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletePartyPulsePastLocalPerformanceWindowAwaitsItsOriginalDeadline(bool close)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 e");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var slow = !close;
        var device = new HostDevice(clock)
        {
            NativeReceipts = true,
            AfterParty = () => { if (slow) clock.Advance(TimeSpan.FromMilliseconds(175)); }
        };
        var source = new CaptureFrameSource(clock);
        var native = new NativeCombatBattleHostIo(flow, device, () =>
        {
            var stamp = source.Next();
            return new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, true, (ulong)stamp.Sequence) { Source = stamp };
        });
        CombatBattleHostInput Request(CombatBattleHostInputKind kind, CaptureFrameStamp stamp) => new(kind, PartyEvidence: true)
        { RequestId = Guid.NewGuid(), Source = stamp, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency * 2 };
        var observed = source.Next();
        if (close)
        {
            var opened = await native.SendAsync(Request(CombatBattleHostInputKind.OpenParty, observed), default);
            Assert.Equal(CombatBattleHostInputStatus.Sent, opened.Status); Assert.Null(opened.Error);
            clock.Advance(TimeSpan.FromMilliseconds(50));
            observed = native.ObservePartyBar().Source;
            slow = true;
        }
        var request = Request(close ? CombatBattleHostInputKind.CloseParty : CombatBattleHostInputKind.OpenParty, observed);
        var result = await native.SendAsync(request, default);
        Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
        Assert.Equal(close ? 4 : 2, result.NativeRequested);
        Assert.Equal(result.NativeRequested, result.NativeSubmitted);
        Assert.False(result.NativeUncertain);
        Assert.Null(result.Error);
        Assert.NotNull(result.PerformanceRecheck);
        Assert.Equal(request.RequestId, result.PerformanceRecheck.Value.RequestId);
        Assert.Equal(request.DeadlineTimestamp, result.PerformanceRecheck.Value.DeadlineTimestamp);
        var beforeCleanup = device.Inputs.ToArray();
        native.ReleaseInput();
        Assert.Equal(beforeCleanup, device.Inputs);
    }
}
