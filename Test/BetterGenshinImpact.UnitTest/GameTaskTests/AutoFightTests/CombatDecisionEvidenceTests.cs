using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatDecisionEvidenceTests
{
    [Fact]
    public async Task OrdinaryPublicationsDoNotCloneAndCancelledHandoffReleasesExactlyOnce()
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock); var battle = Guid.NewGuid();
        using var image = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        void Publish()
        {
            image.FrameStamp = source.Next();
            CombatDecisionEvidence.Publish(scope, image, new(false, false, clock.GetUtcNow().UtcDateTime, null, 2, 2)
            { Source = image.FrameStamp, BattleId = battle, CaptureEpoch = 1, Quality = CombatObservationQuality.Available });
        }
        for (var i = 0; i < 100; i++) Publish();
        Assert.Equal(0, scope.ExactReservationStatistics.Retained);
        using var cancellation = new CancellationTokenSource();
        using var handoff = CombatDecisionEvidence.TryReserve(scope, battle, 1,
            new(CombatBattleHostInputKind.Camera) { RequestId = Guid.NewGuid(), Source = image.FrameStamp, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency }, clock, cancellation.Token);
        for (var i = 0; i < 100; i++) Publish();
        Assert.Equal(1, scope.ExactReservationStatistics.Retained);
        cancellation.Cancel();
        Assert.Null(handoff!.FailureReason);
        Assert.True(handoff.Save("original cancellation"));
        handoff.Dispose(); handoff.Dispose();
        await scope.DisposeAsync();
        Assert.Equal(0, scope.ExactReservationStatistics.Active);
        Assert.Equal(1, scope.ExactReservationStatistics.Released);
    }
    [Fact]
    public async Task PublishedObservationAndPixelsRemainPairedAfterTheProducerMovesOn()
    {
        var saved = new List<(CaptureFrameStamp Source, byte Pixel)>();
        await using var scope = new DiagnosticEvidenceScope((item, image) =>
        { saved.Add((item.Source, image.At<Vec3b>(0, 0).Item0)); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var battle = Guid.NewGuid();
        var input = new CombatBattleHostInput(CombatBattleHostInputKind.Camera, 60)
        { RequestId = Guid.NewGuid(), Source = source.Next(), DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency };
        using var handoff = CombatDecisionEvidence.TryReserve(scope, battle, 1, input, clock, default);
        Assert.NotNull(handoff);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        CaptureFrameStamp stamp;
        using (var image = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(13, 0, 0)), 0, 0) { FrameStamp = source.Next() })
        {
            stamp = image.FrameStamp;
            var observation = new PassiveTargetObservation(false, false, clock.GetUtcNow().UtcDateTime, null, 2, 2)
            { Source = stamp, BattleId = battle, CaptureEpoch = 1, Quality = CombatObservationQuality.Available };
            clock.Advance(TimeSpan.FromMilliseconds(50));
            CombatDecisionEvidence.Publish(scope, image, observation);
        }
        Assert.True(handoff!.TryRead(out var retained));
        Assert.Equal(stamp, retained.Source);
        Assert.True(handoff.Save("test-failure"));
        handoff.Dispose();
        await scope.DisposeAsync();
        Assert.Equal((stamp, (byte)13), Assert.Single(saved));
    }
}
