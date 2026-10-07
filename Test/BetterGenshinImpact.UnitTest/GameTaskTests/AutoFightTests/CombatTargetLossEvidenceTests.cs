using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
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
