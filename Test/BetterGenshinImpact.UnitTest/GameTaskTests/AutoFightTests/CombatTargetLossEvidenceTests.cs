using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight.SkillData;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

[Collection("CombatFlowPerformance")]
public class CombatTargetLossEvidenceTests
{
    [Fact]
    public async Task AcceptedLastProgressKeepsExactPixelsAfterTenSecondHistoryExpires()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var saved = new List<(DiagnosticEvidence Evidence, byte Pixel)>();
        await using var scope = new DiagnosticEvidenceScope((item, image) =>
        { saved.Add((item, image.At<Vec3b>(0, 0).Item0)); return Task.CompletedTask; });
        var seed = CombatSkillCatalog.ParseLocalJson<SkillCatalogSeed>("{\"skills\":[]}");
        using var flow = NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("琴 wait(.1)",
            new SkillCatalogSnapshot(seed.Skills.ToDictionary(skill => skill.Id))), new ProgressFlowGame(), clock);
        var native = new NativeCombatBattleHostIo(flow, new ProgressDevice(clock));
        var episode = "progress-pin";
        var epoch = AvatarRecognition.PassiveCaptureGate.Epoch;
        ImageRegion Frame(byte pixel)
        {
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return new ImageRegion(new Mat(32, 32, MatType.CV_8UC3, new Scalar(pixel, 0, 0)), 0, 0)
            { FrameStamp = source.Next() };
        }
        CombatBattleObservation Observation(ImageRegion frame) =>
            new(frame.FrameStamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 32, 32) { CaptureEpoch = epoch };
        using var a = Frame(11);
        scope.ObserveExistingFrame(a);
        native.Trace(new(flow.Context.BattleId, episode, "Running", "accepted-progress", CombatBattleHostResult.Continue,
            Observation(a), 0, 0, 0, 0, false) { LastProgress = new(a.FrameStamp, "DamageNumber", null, null, clock.GetTimestamp()) });
        using var b = Frame(22);
        scope.ObserveExistingFrame(b);
        var accepted = new CombatProgressEvidence(b.FrameStamp, "DamageNumber", null, null, clock.GetTimestamp());
        native.Trace(new(flow.Context.BattleId, episode, "Running", "accepted-progress", CombatBattleHostResult.Continue,
            Observation(b), 0, 0, 0, 0, false) { LastProgress = accepted });
        clock.Advance(TimeSpan.FromSeconds(25));
        using var c = Frame(33);
        scope.ObserveExistingFrame(c);
        native.Trace(new(flow.Context.BattleId, episode, "Unconfirmed", "no-progress", CombatBattleHostResult.Unconfirmed,
            Observation(c), 0, 0, 25, 0, false, "terminal") { LastProgress = accepted });
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Evidence.Phase == "combat-last-progress" && item.Evidence.Window == null && item.Evidence.Source == b.FrameStamp && item.Pixel == 22);
        Assert.Contains(saved, item => item.Evidence.Phase == "combat-terminal" && item.Evidence.Source == c.FrameStamp && item.Pixel == 33);
        Assert.DoesNotContain(saved, item => item.Evidence.Phase == "combat-last-progress" && item.Evidence.Window == null && item.Evidence.Source != b.FrameStamp);
        Assert.All(saved.Where(item => item.Evidence.Phase == "combat-last-progress" && item.Evidence.Window != null), item =>
        {
            Assert.Equal(b.FrameStamp, item.Evidence.Window!.Anchor);
            Assert.InRange(item.Evidence.Window.RelativeSeconds, -10d, 5d);
        });
    }

    private sealed class ProgressFlowGame : ICombatFlowGame
    {
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct) => throw new InvalidOperationException("No flow action in evidence lifecycle.");
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => null;
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }
    private sealed class ProgressDevice(FakeTimeProvider clock) : ICombatHostInputDevice
    {
        public TimeProvider Clock => clock;
        public Microsoft.Extensions.Logging.ILogger Logger => NullLogger.Instance;
        public void PrepareInput() => throw new InvalidOperationException("No native input.");
        public void MoveCamera(int x, int y) => throw new InvalidOperationException("No native input.");
        public void PressTargetLock() => throw new InvalidOperationException("No native input.");
        public void MoveForward(bool down) => throw new InvalidOperationException("No native input.");
        public void PressDrop() => throw new InvalidOperationException("No native input.");
        public void PressParty() => throw new InvalidOperationException("No native input.");
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct) => throw new InvalidOperationException("No action wait.");
    }

    [Fact]
    public async Task ExactSourceSlotKeepsBOutsideHistoryAndReleasesOnForget()
    {
        var saved = new List<(DiagnosticEvidence Evidence, byte Pixel)>();
        await using var scope = new DiagnosticEvidenceScope((item, image) => { saved.Add((item, image.At<Vec3b>(0, 0).Item0)); return Task.CompletedTask; });
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var a = new ImageRegion(new Mat(32, 32, MatType.CV_8UC3, new Scalar(11, 0, 0)), 0, 0) { FrameStamp = source.Next() };
        scope.ObserveExistingFrame(a); Assert.True(scope.RememberExactSource("combat-last-progress", "episode", a.FrameStamp));
        clock.Advance(TimeSpan.FromSeconds(1));
        using var b = new ImageRegion(new Mat(32, 32, MatType.CV_8UC3, new Scalar(22, 0, 0)), 0, 0) { FrameStamp = source.Next() };
        scope.ObserveExistingFrame(b); Assert.True(scope.RememberExactSource("combat-last-progress", "episode", b.FrameStamp));
        Assert.False(scope.CaptureExactWindow("combat-last-progress", "episode", a.FrameStamp, "wrong-source", "must not substitute"));
        clock.Advance(TimeSpan.FromSeconds(25));
        using var c = new ImageRegion(new Mat(32, 32, MatType.CV_8UC3, new Scalar(33, 0, 0)), 0, 0) { FrameStamp = source.Next() };
        scope.ObserveExistingFrame(c);
        Assert.True(scope.RememberExactSource("combat-last-progress", "episode", b.FrameStamp));
        Assert.True(scope.CaptureExactWindow("combat-last-progress", "episode", b.FrameStamp, "last-progress", "exact B"));
        scope.ForgetExactFrame("combat-last-progress", "episode");
        Assert.False(scope.CaptureExactWindow("combat-last-progress", "episode", b.FrameStamp, "after-forget", "must release"));
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Evidence.Phase == "last-progress" && item.Evidence.Window == null && item.Evidence.Source == b.FrameStamp && item.Pixel == 22);
        Assert.DoesNotContain(saved, item => item.Evidence.Phase == "last-progress" && item.Evidence.Window == null && item.Evidence.Source != b.FrameStamp);
    }

    [Fact]
    public async Task RejectedRawObservationCannotDiscardAnAlreadyAcceptedSameEpisodeProgress()
    {
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        var saved = new List<(DiagnosticEvidence Evidence, byte Pixel)>();
        await using var scope = new DiagnosticEvidenceScope((item, image) =>
        { saved.Add((item, image.At<Vec3b>(0, 0).Item0)); return Task.CompletedTask; });
        using var flow = ProgressFlow(clock);
        var native = new NativeCombatBattleHostIo(flow, new ProgressDevice(clock));
        const string episode = "accepted-before-raw-rejection";
        using var a = ProgressFrame(source, 11);
        scope.ObserveExistingFrame(a);
        native.Trace(new(flow.Context.BattleId, episode, "Running", "accepted-progress", CombatBattleHostResult.Continue,
            ProgressObservation(a, flow.Context.BattleId), 0, 0, 0, 0, false)
            { LastProgress = new(a.FrameStamp, "new-damage-cue", null, null, clock.GetTimestamp()) });
        clock.Advance(TimeSpan.FromMilliseconds(100));
        using var b = ProgressFrame(source, 22);
        scope.ObserveExistingFrame(b);
        var acceptedObservation = ProgressObservation(b, flow.Context.BattleId);
        var accepted = new CombatProgressEvidence(b.FrameStamp, "new-damage-cue", null, null, clock.GetTimestamp());
        native.Trace(new(flow.Context.BattleId, episode, "Running", "accepted-progress", CombatBattleHostResult.Continue,
            acceptedObservation, 0, 0, 0, 0, false) { LastProgress = accepted });

        clock.Advance(TimeSpan.FromMilliseconds(100));
        var rejected = acceptedObservation with { CaptureEpoch = acceptedObservation.CaptureEpoch + 1 };
        // This raw observation was rejected. It does not publish a frame or replace the host's accepted progress.
        native.Trace(new(flow.Context.BattleId, episode, "Running", "raw-observation-rejected", CombatBattleHostResult.Continue,
            rejected, 0, 0, .1, 0, false) { LastProgress = accepted, ObservationGate = "rejected-epoch" });

        clock.Advance(TimeSpan.FromSeconds(25));
        using var c = ProgressFrame(source, 33);
        scope.ObserveExistingFrame(c);
        native.Trace(new(flow.Context.BattleId, episode, "Unconfirmed", "no-progress", CombatBattleHostResult.Unconfirmed,
            ProgressObservation(c, flow.Context.BattleId), 0, 0, 25.1, 0, false, "terminal") { LastProgress = accepted });
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Evidence.Phase == "combat-last-progress" && item.Evidence.Window == null && item.Evidence.Source == b.FrameStamp && item.Pixel == 22);
        Assert.Contains(saved, item => item.Evidence.Phase == "combat-terminal" && item.Evidence.Source == c.FrameStamp && item.Pixel == 33);
        Assert.DoesNotContain(saved, item => item.Evidence.Phase == "combat-last-progress" && item.Evidence.Window == null && item.Evidence.Source != b.FrameStamp);
    }

    [Fact]
    public async Task RawDamageCueWithoutAcceptedProgressNeverPinsItsFrame()
    {
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var flow = ProgressFlow(clock);
        var native = new NativeCombatBattleHostIo(flow, new ProgressDevice(clock));
        using var b = ProgressFrame(source, 22);
        scope.ObserveExistingFrame(b);
        var raw = ProgressObservation(b, flow.Context.BattleId) with
        { Target = new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(4, 4, 8, 22, 176), 1, SeekCueKind.DamageNumber) };
        native.Trace(new(flow.Context.BattleId, "raw", "Running", "raw-cue-only", CombatBattleHostResult.Continue, raw, 0, 0, 0, 0, false));
        Assert.False(scope.CaptureExactWindow("combat-last-progress", "battle:" + flow.Context.BattleId.ToString("N") + ":raw",
            b.FrameStamp, "unexpected-pin", "raw cue is not accepted progress"));
        clock.Advance(TimeSpan.FromSeconds(25));
        using var c = ProgressFrame(source, 33);
        scope.ObserveExistingFrame(c);
        native.Trace(new(flow.Context.BattleId, "raw", "Unconfirmed", "no-progress", CombatBattleHostResult.Unconfirmed,
            ProgressObservation(c, flow.Context.BattleId), 0, 0, 25, 0, false, "terminal"));
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Phase == "combat-terminal" && item.Source == c.FrameStamp);
        Assert.DoesNotContain(saved, item => item.Phase == "combat-last-progress" && item.Window == null);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("battle")]
    [InlineData("epoch")]
    [InlineData("session")]
    public async Task MissingOrForeignProgressNeverSubstitutesTheTerminalSource(string defect)
    {
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var flow = ProgressFlow(clock);
        var native = new NativeCombatBattleHostIo(flow, new ProgressDevice(clock));
        using var b = ProgressFrame(source, 22);
        if (defect != "missing") scope.ObserveExistingFrame(b);
        var observation = ProgressObservation(b, flow.Context.BattleId);
        if (defect == "battle") observation = observation with { BattleId = Guid.NewGuid() };
        if (defect == "epoch") observation = observation with { CaptureEpoch = observation.CaptureEpoch + 1 };
        var progressSource = defect == "session" ? new CaptureFrameSource(clock).Next() : b.FrameStamp;
        var progress = new CombatProgressEvidence(progressSource, "new-damage-cue", null, null, clock.GetTimestamp());
        native.RetainAcceptedProgress(progress, observation, "invalid");
        var request = "battle:" + flow.Context.BattleId.ToString("N") + ":invalid";
        Assert.False(scope.CaptureExactWindow("combat-last-progress", request, progressSource, "unexpected-pin", defect));
        clock.Advance(TimeSpan.FromSeconds(25));
        using var c = ProgressFrame(source, 33);
        scope.ObserveExistingFrame(c);
        native.Trace(new(flow.Context.BattleId, "invalid", "Unconfirmed", "no-progress", CombatBattleHostResult.Unconfirmed,
            ProgressObservation(c, flow.Context.BattleId), 0, 0, 25, 0, false, "terminal") { LastProgress = progress });
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Phase == "combat-terminal" && item.Source == c.FrameStamp);
        Assert.DoesNotContain(saved, item => item.Phase == "combat-last-progress" && item.Window == null);
    }

    [Fact]
    public async Task ProgressSharesTheOriginalTwoExactSlotsAndForgetMakesRoom()
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var frame = ProgressFrame(source, 22);
        scope.ObserveExistingFrame(frame);
        scope.RememberExactFrame("other-a", "a", frame);
        scope.RememberExactFrame("other-b", "b", frame);
        Assert.False(scope.RememberExactSource("combat-last-progress", "progress", frame.FrameStamp));
        Assert.True(scope.CaptureExactWindow("other-a", "a", frame.FrameStamp, "a", "existing owner remains"));
        scope.ForgetExactFrame("other-a", "a");
        Assert.True(scope.RememberExactSource("combat-last-progress", "progress", frame.FrameStamp));
        scope.RememberExactFrame("third", "c", frame);
        Assert.False(scope.CaptureExactWindow("third", "c", frame.FrameStamp, "c", "must not add third slot"));
        scope.ForgetExactFrame("combat-last-progress", "progress");
        scope.RememberExactFrame("third", "c", frame);
        Assert.True(scope.CaptureExactWindow("third", "c", frame.FrameStamp, "c", "forget releases slot"));
    }

    [Fact]
    public async Task ProgressCannotBypassTheExistingByteBudget()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; }, maximumBytes: 1);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var frame = ProgressFrame(source, 22);
        scope.ObserveExistingFrame(frame);
        Assert.False(scope.RememberExactSource("combat-last-progress", "too-large", frame.FrameStamp));
        Assert.False(scope.CaptureExactWindow("combat-last-progress", "too-large", frame.FrameStamp, "forbidden", "budget unchanged"));
        await scope.DisposeAsync();
        Assert.Empty(saved);
    }

    [Fact]
    public async Task ClosedEpisodeReleasesTheNativeProgressSlot()
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var flow = ProgressFlow(clock);
        var native = new NativeCombatBattleHostIo(flow, new ProgressDevice(clock));
        using var frame = ProgressFrame(source, 22);
        scope.ObserveExistingFrame(frame);
        var observation = ProgressObservation(frame, flow.Context.BattleId);
        native.RetainAcceptedProgress(new(frame.FrameStamp, "new-damage-cue", null, null, clock.GetTimestamp()), observation, "closed");
        native.Trace(new(flow.Context.BattleId, "closed", "Running", "episode-closed", CombatBattleHostResult.Continue,
            observation, 0, 0, 0, 0, false) { ClosedEpisode = "closed" });
        Assert.False(scope.CaptureExactWindow("combat-last-progress", "battle:" + flow.Context.BattleId.ToString("N") + ":closed",
            frame.FrameStamp, "after-close", "must release"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostDisposeAndCancelledExitReleaseProgressWithoutNativeInput(bool cancelled)
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var flow = ProgressFlow(clock);
        var native = new NativeCombatBattleHostIo(flow, new ProgressDevice(clock));
        using var frame = ProgressFrame(source, 22);
        scope.ObserveExistingFrame(frame);
        var observation = ProgressObservation(frame, flow.Context.BattleId);
        native.RetainAcceptedProgress(new(frame.FrameStamp, "new-damage-cue", null, null, clock.GetTimestamp()), observation, "dispose");
        var boundary = new ProgressHostBoundary(native, observation, clock);
        using var host = new CombatBattleHost(boundary, new());
        if (cancelled)
        {
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await host.AdvanceAsync(flow, cts.Token); });
        }
        host.Dispose();
        Assert.False(scope.CaptureExactWindow("combat-last-progress", "battle:" + flow.Context.BattleId.ToString("N") + ":dispose",
            frame.FrameStamp, "after-dispose", "must release"));
    }

    [Fact]
    public async Task DiagnosticForgetFailureCannotReplaceTheOriginalReleaseFailure()
    {
        var clock = new FakeTimeProvider();
        using var flow = ProgressFlow(clock);
        var native = new NativeCombatBattleHostIo(flow, new ProgressDevice(clock));
        var release = new InvalidOperationException("original release boundary failure");
        var boundary = new ProgressHostBoundary(native, default, clock)
        { ReleaseError = release, DiagnosticError = new IOException("diagnostic boundary failure") };
        using var host = new CombatBattleHost(boundary, new());
        Assert.Same(release, Assert.Throws<InvalidOperationException>(host.Dispose));
    }

    private static NativeCombatFlowRunner ProgressFlow(FakeTimeProvider clock)
    {
        var seed = CombatSkillCatalog.ParseLocalJson<SkillCatalogSeed>("{\"skills\":[]}");
        return NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("琴 wait(.1)",
            new SkillCatalogSnapshot(seed.Skills.ToDictionary(skill => skill.Id))), new ProgressFlowGame(), clock);
    }
    private static ImageRegion ProgressFrame(CaptureFrameSource source, byte pixel) =>
        new(new Mat(32, 32, MatType.CV_8UC3, new Scalar(pixel, 0, 0)), 0, 0) { FrameStamp = source.Next() };
    private static CombatBattleObservation ProgressObservation(ImageRegion frame, Guid battle) =>
        new(frame.FrameStamp, battle, CombatObservationQuality.Available, null, 32, 32) { CaptureEpoch = AvatarRecognition.PassiveCaptureGate.Epoch };
    private sealed class ProgressHostBoundary(NativeCombatBattleHostIo native, CombatBattleObservation observation, FakeTimeProvider clock) : ICombatBattleHostIo
    {
        public Exception? ReleaseError { get; init; }
        public Exception? DiagnosticError { get; init; }
        public TimeProvider Clock => clock;
        public Guid BattleId => native.BattleId;
        public CombatBattleObservation ObserveTarget() => observation;
        public PartySetupFinishObservation ObservePartyBar() => default;
        public ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct) => throw new InvalidOperationException("No native input.");
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
        public void Trace(CombatBattleHostTrace trace) => native.Trace(trace);
        public void ReleaseInput() { if (ReleaseError != null) throw ReleaseError; }
        public void ForgetEpisodeEvidence() { native.ForgetEpisodeEvidence(); if (DiagnosticError != null) throw DiagnosticError; }
    }

    [Fact]
    public async Task ProductionPublicationKeepsItsOwnSourceWhenTheSharedLatestIsCleared()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var battle = Guid.NewGuid();
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var collector = new CombatTargetLossEvidence(battle, clock, NullLogger.Instance);
        using var frame = new ImageRegion(new Mat(64, 64, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        var gate = AvatarRecognition.PassiveCaptureGate;
        try
        {
            Assert.True(AvatarRecognition.PublishPassiveObservation(false, false, null, 64, 64,
                clock.GetUtcNow().UtcDateTime, gate.Epoch, source: frame.FrameStamp, battleId: battle,
                evidence: observed =>
                {
                    AvatarRecognition.ClearPassiveObservation();
                    collector.ObservePublished(frame, observed);
                }));
            await evidence.DisposeAsync();
            Assert.Equal(frame.FrameStamp, Assert.Single(saved).Source);
            Assert.True(AvatarRecognition.PublishPassiveObservation(false, false, null, 64, 64,
                clock.GetUtcNow().UtcDateTime, gate.Epoch, source: frame.FrameStamp, battleId: battle,
                evidence: _ => throw new IOException("diagnostic fixture")));
        }
        finally { AvatarRecognition.ClearPassiveObservation(); }
    }

    [Fact]
    public async Task TargetLossSavesOriginalDecisionAndLastPositiveCropWithoutAnyLaterCapture()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var battle = Guid.NewGuid();
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var collector = new CombatTargetLossEvidence(battle, clock, NullLogger.Instance);
        using var frame = new ImageRegion(new Mat(64, 64, MatType.CV_8UC3, Scalar.Black), 0, 0);
        void Publish(bool target)
        {
            clock.Advance(TimeSpan.FromMilliseconds(60));
            frame.FrameStamp = source.Next();
            collector.ObservePublished(frame, new(target, false, clock.GetUtcNow().UtcDateTime,
                target ? new EnemySeekVisual(24, 24, 12, 8, 96) : null, 64, 64)
            { Source = frame.FrameStamp, BattleId = battle, Quality = CombatObservationQuality.Available,
                TargetAbsenceReason = target ? null : "all-visual-candidates-filtered",
                Recognition = new() { RawComponents = 17, Accepted = target ? 1 : 0 } });
        }
        Publish(true);
        Publish(true);
        var lastPositive = frame.FrameStamp;
        Publish(false);
        var lost = frame.FrameStamp;
        Publish(false);
        Publish(true);
        Publish(false);
        collector.Dispose();
        await evidence.DisposeAsync();
        var before = Assert.Single(saved.Where(item => item.Phase == "before-combat-target-positive"));
        Assert.Equal(lastPositive, before.Source);
        Assert.Contains("sourceRect=", before.Detail);
        var original = Assert.Single(saved.Where(item => item.Phase == "perception-target-lost" && item.Window == null));
        Assert.Equal(lost, original.Source);
        Assert.Equal("all-visual-candidates-filtered", original.Fields!["absenceReason"]);
        Assert.Contains(lastPositive.Sequence.ToString(), original.Fields["lastTargetSource"]);
        Assert.Single(saved.Where(item => item.Phase == "perception-target-returned"));
        Assert.False(frame.SrcMat.IsDisposed);
    }

    [Fact]
    public async Task DifferentBattleOrMismatchedSourceCannotCreateTargetLossEvidence()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var battle = Guid.NewGuid();
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var collector = new CombatTargetLossEvidence(battle, clock, NullLogger.Instance);
        using var frame = new ImageRegion(new Mat(64, 64, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        var observed = new PassiveTargetObservation(false, false, clock.GetUtcNow().UtcDateTime, null, 64, 64)
            { Source = frame.FrameStamp, BattleId = Guid.NewGuid(), Quality = CombatObservationQuality.Available };
        collector.ObservePublished(frame, observed);
        collector.ObservePublished(frame, observed with { BattleId = battle, Source = source.Next() });
        await evidence.DisposeAsync();
        Assert.Empty(saved);
    }
}
