using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalTraceUsesExistingPixelsAndLabelsMissingDecisionSource(bool exactDecisionRetained)
    {
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), physical);
        var saved = new List<(DiagnosticEvidence Evidence, byte Pixel)>();
        await using var scope = new DiagnosticEvidenceScope((item, image) =>
        { saved.Add((item, image.At<Vec3b>(0, 0).Item0)); return Task.CompletedTask; });
        var source = new CaptureFrameSource(clock);
        using var first = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(7, 0, 0)), 0, 0)
        { FrameStamp = source.Next() };
        scope.ObserveExistingFrame(first);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        using var decision = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(19, 0, 0)), 0, 0)
        { FrameStamp = source.Next() };
        scope.ObserveExistingFrame(decision); // Sparse history deliberately has no pixels for this source.
        if (exactDecisionRetained) scope.RememberExactFrame("fixture", "decision", decision);
        clock.Advance(TimeSpan.FromMilliseconds(100));
        using var later = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(31, 0, 0)), 0, 0)
        { FrameStamp = source.Next() };
        scope.ObserveExistingFrame(later); // Must never be relabelled as the decision anchor.
        var native = new NativeCombatBattleHostIo(flow, new HostDevice(clock));
        native.Trace(new(flow.Context.BattleId, "search", "Unconfirmed", "no-progress", CombatBattleHostResult.Unconfirmed,
            new(decision.FrameStamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 2, 2),
            24, 12, 15, 0, true, "terminal"));
        await scope.DisposeAsync();
        var anchor = Assert.Single(saved.Where(item => item.Evidence.Phase == "combat-terminal" && item.Evidence.Window?.RelativeIndex == 0));
        var expected = exactDecisionRetained ? decision.FrameStamp : first.FrameStamp;
        Assert.Equal(expected, anchor.Evidence.Source);
        Assert.Equal(exactDecisionRetained ? 19 : 7, anchor.Pixel);
        Assert.Equal($"{decision.FrameStamp.SessionId}/{decision.FrameStamp.Sequence}", anchor.Evidence.Fields!["requestedDecisionSource"]);
        Assert.Equal($"{expected.SessionId}/{expected.Sequence}", anchor.Evidence.Fields["actualContextSource"]);
        Assert.Equal(exactDecisionRetained ? "exact-decision-source" : "unavailable:nearby-context-only", anchor.Evidence.Fields["decisionSourcePixels"]);
        Assert.Equal(exactDecisionRetained ? "0.000" : "-0.100", anchor.Evidence.Fields["contextOffsetSeconds"]);
    }

    [Theory]
    [InlineData("session")]
    [InlineData("clock")]
    [InlineData("expired-context")]
    public async Task TerminalTraceCannotUseUnrelatedOrExpiredContextPixels(string mismatch)
    {
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), physical);
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        scope.ObserveExistingFrame(frame);
        clock.Advance(TimeSpan.FromSeconds(mismatch == "expired-context" ? 11 : .1));
        var requested = source.Next();
        if (mismatch == "session") requested = requested with { SessionId = Guid.NewGuid() };
        if (mismatch == "clock") requested = requested with { TimestampFrequency = requested.TimestampFrequency * 2 };
        var native = new NativeCombatBattleHostIo(flow, new HostDevice(clock));
        native.Trace(new(flow.Context.BattleId, "search", "Unconfirmed", "no-progress", CombatBattleHostResult.Unconfirmed,
            new(requested, flow.Context.BattleId, CombatObservationQuality.Available, null, 2, 2),
            24, 12, 15, 0, true, "terminal"));
        await scope.DisposeAsync();
        Assert.DoesNotContain(saved, item => item.Phase == "combat-terminal");
    }

    [Fact]
    public async Task PartyProbeReleaseSavesItsActualLastFrameAndFreesTheExactSlots()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), physical);
        var sample = default(PartySetupFinishObservation);
        var native = new NativeCombatBattleHostIo(flow, new HostDevice(clock) { NativeReceipts = true }, partyObservation: () => sample);
        var input = new CombatBattleHostInput(CombatBattleHostInputKind.OpenParty)
        { RequestId = Guid.NewGuid(), Source = source.Next(), DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency };
        var result = await native.SendAsync(input, default);
        Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
        clock.Advance(TimeSpan.FromMilliseconds(70));
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(19, 0, 0)), 0, 0) { FrameStamp = source.Next() };
        sample = new(frame.FrameStamp.Sequence, frame.FrameStamp.CapturedAt, 2, 2, false, 0) { Source = frame.FrameStamp };
        native.ObservePartyBar();
        NativeCombatBattleHostIo.CapturePartyProbe(frame, sample, true, input.RequestId,
            new CaptureFrameFence(input.Source, result.CompletedTimestamp!.Value), clock,
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, flow.Context.BattleId, admittedAtDecision: true);
        native.ReleaseInput();
        using var reservation = scope.TryReserveExactFrame(new(flow.Context.BattleId, Guid.NewGuid(), 1), frame.FrameStamp,
            clock, clock.GetTimestamp() + clock.TimestampFrequency, default, out var rejection);
        Assert.NotNull(reservation);
        Assert.Null(rejection);
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Phase == "party-terminal" && item.Source == frame.FrameStamp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeHostTurnsToOffscreenTargetThenApproachesAndExecutesTheOriginalAttack(bool collect)
    {
        var clock = new FakeTimeProvider();
        var program = LoadProgram("琴 attack(.1)");
        using var physical = new PhysicalReplay(clock, false, 50, program);
        physical.SetFrontActor("琴");
        using var flow = NativeCombatFlowRunner.Create(program, physical);
        var turned = false;
        var approached = false;
        var saved = new List<DiagnosticEvidence>();
        await using var scope = collect ? new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; }) : null;
        var device = new HostDevice(clock) { NativeReceipts = true,
            AfterCamera = () =>
            {
                turned = true;
                new Fischless.WindowsInput.WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0)
                    .DispatchInput(new Vanara.PInvoke.User32.INPUT[1]);
            },
            AfterForward = down => { if (!down) approached = true; },
            AdvanceClock = ms => physical.DelayAsync(ms, default).GetAwaiter().GetResult()
        };
        var source = new CaptureFrameSource(clock);
        CombatBattleObservation Observe()
        {
            var stamp = source.Next();
            var visual = turned ? new EnemySeekVisual(920, 400, 80, approached ? 8 : 3, 240) :
                new EnemySeekVisual(447, 408, 33, 30, 392, -90);
            var decision = new EnemySeekDecision(turned ? AutoFightSeekAction.ApproachVisibleEnemy : AutoFightSeekAction.Approach,
                turned ? EnemyIndicatorDirection.None : EnemyIndicatorDirection.Left, visual, 1,
                turned ? SeekCueKind.HealthBar : SeekCueKind.DirectionIndicator);
            using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = stamp };
            CombatDecisionEvidence.Publish(scope, frame, new(turned, false, clock.GetUtcNow().UtcDateTime, turned ? visual : null, 1920, 1080,
                turned ? null : decision) { Source = stamp, BattleId = flow.Context.BattleId,
                CaptureEpoch = AvatarRecognition.PassiveCaptureGate.Epoch, Quality = CombatObservationQuality.Available });
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, decision, 1920, 1080)
            { Motion = BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal,
                Control = new(BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal, false) };
        }
        var native = new NativeCombatBattleHostIo(flow, device, targetObservation: Observe);
        using var host = new CombatBattleHost(native, new() { FinishDetectionEnabled = false });
        for (var i = 0; i < 200 && !physical.Primitives.Any(input => input.Method == BetterGenshinImpact.GameTask.AutoFight.Script.Method.Attack); i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.True(turned && approached);
        Assert.Contains(physical.Primitives, input => input.Method == BetterGenshinImpact.GameTask.AutoFight.Script.Method.Attack);
        Assert.InRange(host.ApproachRequests, 1, 12);
        Assert.InRange(host.CameraRequests, 1, 24);
        Assert.DoesNotContain("party", device.Inputs);
        host.Dispose();
        if (scope != null)
        {
            await scope.DisposeAsync();
            Assert.Contains(saved, item => item.Phase == "host-input-feedback" && item.Fields!["inputIntent"].Contains("Approach"));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeInputFeedbackKeepsTheActualPostInputPixelsAndReceiptBeforeProducerDisposal(bool hostReadsBeforeEvidenceCallback)
    {
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), physical);
        var saved = new List<(DiagnosticEvidence Evidence, byte Pixel)>();
        await using var scope = new DiagnosticEvidenceScope((item, image) =>
        { saved.Add((item, image.At<Vec3b>(0, 0).Item0)); return Task.CompletedTask; });
        var source = new CaptureFrameSource(clock);
        var latest = source.Next();
        var native = new NativeCombatBattleHostIo(flow, new HostDevice(clock) { NativeReceipts = true,
            AfterCamera = () => new Fischless.WindowsInput.WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0)
                .DispatchInput(new Vanara.PInvoke.User32.INPUT[1]) },
            targetObservation: () => new(latest, flow.Context.BattleId, CombatObservationQuality.Available, null, 2, 2));
        var input = new CombatBattleHostInput(CombatBattleHostInputKind.Camera, 60)
        { RequestId = Guid.NewGuid(), Source = latest, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency };
        void Publish(byte pixel, int advance)
        {
            clock.Advance(TimeSpan.FromMilliseconds(advance));
            latest = source.Next();
            using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(pixel, 0, 0)), 0, 0) { FrameStamp = latest };
            if (pixel == 33 && hostReadsBeforeEvidenceCallback) native.ConfirmInputFeedback(native.ObserveTarget());
            // AvatarRecognition feeds its existing producer frame before publishing the observation.
            scope.CaptureRequestedFrames(flow.Context.BattleId.ToString("N"), frame);
            CombatDecisionEvidence.Publish(scope, frame, new(false, false, clock.GetUtcNow().UtcDateTime, null, 2, 2)
            { Source = latest, BattleId = flow.Context.BattleId, CaptureEpoch = AvatarRecognition.PassiveCaptureGate.Epoch,
                Quality = CombatObservationQuality.Available });
        }
        native.PrepareInputEvidence(input, default);
        Publish(11, 50);
        input = input with { Source = latest };
        native.PrepareInputEvidence(input, default);
        var result = await native.SendAsync(input, default);
        Assert.True(result.HasCompleteReceipt);
        Publish(22, 30); // Too early: cannot stand in for feedback.
        Publish(33, 70);
        var feedback = latest;
        native.ConfirmInputFeedback(native.ObserveTarget());
        Publish(44, 200); // Unrelated later pixels cannot replace the original pair.
        Publish(55, 1000); // Settled context, not a second target/damage observation.
        Publish(66, 1000);
        native.ReleaseInput();
        await scope.DisposeAsync();
        var after = Assert.Single(saved.Where(item => item.Evidence.Phase == "host-input-feedback"));
        Assert.Equal(feedback, after.Evidence.Source);
        Assert.Equal(33, after.Pixel);
        Assert.Contains(input.Source.Sequence.ToString(), after.Evidence.Fields!["inputSource"]);
        Assert.Contains("Sent", after.Evidence.Fields["inputReceipt"]);
        Assert.Contains(saved, item => item.Evidence.Source == input.Source && item.Pixel == 11);
        var context = saved.Where(item => item.Evidence.Phase == "host-input-context").ToArray();
        Assert.Contains(context, item => item.Pixel == 55 && item.Evidence.Window?.RelativeSeconds >= 1);
        Assert.Contains(context, item => item.Pixel == 66 && item.Evidence.Window?.RelativeSeconds >= 2);
        Assert.All(context, item =>
        {
            Assert.Equal(input.RequestId.ToString("N"), item.Evidence.Fields!["inputRequest"]);
            Assert.Equal(DiagnosticEvidencePriority.Routine, item.Evidence.Priority);
            Assert.Equal($"{feedback.SessionId}/{feedback.Sequence}", item.Evidence.Fields!["contextAnchorSource"]);
            Assert.True(item.Evidence.Fields.ContainsKey("anchorFeedbackTarget"));
            Assert.False(item.Evidence.Fields.ContainsKey("feedbackTarget"));
        });
        Assert.Equal(0, scope.ExactReservationStatistics.Active);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("normal")]
    [InlineData("full")]
    [InlineData("slow")]
    [InlineData("failed")]
    [InlineData("different-frame")]
    public async Task NativeDiagnosticReservationsNeverChooseBusinessFramesOrDelayInput(string mode)
    {
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), physical);
        var saved = new List<(CaptureFrameStamp Source, byte Pixel)>();
        var writer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scope = mode == "off" ? null : new DiagnosticEvidenceScope(async (item, image) =>
        {
            if (mode == "slow") await writer.Task;
            if (mode == "failed") throw new IOException("test-writer-failure");
            saved.Add((item.Source, image.At<Vec3b>(0, 0).Item0));
        });
        var source = new CaptureFrameSource(clock);
        var latest = source.Next();
        var device = new HostDevice(clock);
        var native = new NativeCombatBattleHostIo(flow, device, targetObservation: () =>
            new(latest, flow.Context.BattleId, CombatObservationQuality.Available, null, 2, 2));
        var input = new CombatBattleHostInput(CombatBattleHostInputKind.Camera, 60)
        { RequestId = Guid.NewGuid(), Source = latest, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency };
        var blockers = new List<DiagnosticFrameReservation>();
        try
        {
            if (mode == "full")
                for (var i = 0; i < 2; i++) blockers.Add(scope!.TryReserveExactFrame(
                    new(flow.Context.BattleId, Guid.NewGuid(), AvatarRecognition.PassiveCaptureGate.Epoch), latest,
                    clock, input.DeadlineTimestamp, default)!);
            _ = native.PrepareInputEvidence(input, default);
            clock.Advance(TimeSpan.FromMilliseconds(50));
            latest = source.Next();
            using var pixels = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(27, 0, 0)), 0, 0) { FrameStamp = latest };
            CombatDecisionEvidence.Publish(scope, pixels, new(false, false, clock.GetUtcNow().UtcDateTime, null, 2, 2)
            { Source = latest, BattleId = flow.Context.BattleId, CaptureEpoch = AvatarRecognition.PassiveCaptureGate.Epoch,
                Quality = CombatObservationQuality.Available });
            if (mode == "different-frame") latest = source.Next();
            Assert.Equal(latest, native.ObserveTarget().Source);
            input = input with { Source = latest };
            _ = native.PrepareInputEvidence(input, default);
            var before = clock.GetTimestamp();
            var result = await native.SendAsync(input, default);
            Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
            Assert.Equal(before, result.CompletedTimestamp);
            Assert.Equal("camera", Assert.Single(device.Inputs));
            native.EndInputEvidence("test-terminal");
            writer.TrySetResult();
        }
        finally
        {
            writer.TrySetResult();
            native.ReleaseInput();
            foreach (var blocker in blockers) blocker.Dispose();
        }
        if (scope != null)
        {
            await scope.DisposeAsync();
            Assert.Equal(0, scope.ExactReservationStatistics.Active);
        }
        if (mode is "normal" or "slow") Assert.Equal((input.Source, (byte)27), Assert.Single(saved));
        else Assert.Empty(saved);
    }
}
