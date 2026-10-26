using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatInputFeedbackEvidenceTests
{
    [Fact]
    public async Task RepeatedSuccessfulInputsShareContextButKeepEveryExactFeedback()
    {
        var saved = new List<(DiagnosticEvidence Evidence, byte Pixel)>();
        await using var scope = new DiagnosticEvidenceScope((item, image) =>
        { saved.Add((item, image.At<Vec3b>(0, 0).Item0)); return Task.CompletedTask; });
        var expected = new Dictionary<string, (CaptureFrameStamp Source, byte Pixel, string Receipt)>();
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var battle = Guid.NewGuid();
        for (byte i = 0; i < 10; i++)
        {
            var input = new CombatBattleHostInput(CombatBattleHostInputKind.Camera)
            { Source = source.Next(), RequestId = Guid.NewGuid(), DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency };
            var receipt = new CombatBattleHostInputResult(CombatBattleHostInputStatus.Sent, clock.GetTimestamp())
            { NativeRequested = 1, NativeSubmitted = 1 };
            CombatInputFeedbackEvidence.Arm(scope, battle, 1, input, receipt, clock, NullLogger.Instance);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(i, 0, 0)), 0, 0) { FrameStamp = source.Next() };
            expected.Add(input.RequestId.ToString("N"), (frame.FrameStamp, i, receipt.ToString()));
            var observed = new PassiveTargetObservation(false, false, clock.GetUtcNow().UtcDateTime, null, 2, 2)
            { Source = frame.FrameStamp, BattleId = battle, CaptureEpoch = 1, Quality = CombatObservationQuality.Available };
            CombatDecisionEvidence.Publish(scope, frame, observed);
        }
        CombatInputFeedbackEvidence.End(scope, battle);
        await scope.DisposeAsync();
        var feedback = saved.Where(item => item.Evidence.Phase == "host-input-feedback").ToArray();
        Assert.Equal(10, feedback.Length);
        Assert.Equal(10, feedback.Select(item => item.Evidence.Request).Distinct().Count());
        Assert.All(feedback, item =>
        {
            var original = expected[item.Evidence.Request];
            Assert.Equal(original.Source, item.Evidence.Source);
            Assert.Equal(original.Pixel, item.Pixel);
            Assert.Equal(original.Receipt, item.Evidence.Fields!["inputReceipt"]);
        });
        var context = saved.Where(item => item.Evidence.Phase == "host-input-context").ToArray();
        Assert.NotEmpty(context);
        Assert.True(context.Length <= 4, $"unchanged context was duplicated {context.Length} times");
        Assert.Single(context.Select(item => item.Evidence.Window!.WindowId).Distinct());
        Assert.Contains(context, item => item.Evidence.Window is { Occurrence: "first", RelativeIndex: 0 } && item.Pixel == 0);
        Assert.Contains(context, item => item.Evidence.Window is { Occurrence: "last", RelativeIndex: 0 } && item.Pixel == 9);
        Assert.Equal(0, scope.ExactReservationStatistics.Active);
    }

    [Fact]
    public async Task SharedContextKeepsFeedbackStateChangeAndFinalOccurrence()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var battle = Guid.NewGuid();
        var feedbackSources = new List<CaptureFrameStamp>();
        for (var i = 0; i < 6; i++)
        {
            var input = new CombatBattleHostInput(CombatBattleHostInputKind.Camera)
            { Source = source.Next(), RequestId = Guid.NewGuid(), DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency };
            var receipt = new CombatBattleHostInputResult(CombatBattleHostInputStatus.Sent, clock.GetTimestamp())
            { NativeRequested = 1, NativeSubmitted = 1 };
            CombatInputFeedbackEvidence.Arm(scope, battle, 1, input, receipt, clock, NullLogger.Instance);
            clock.Advance(TimeSpan.FromMilliseconds(100));
            using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
            feedbackSources.Add(frame.FrameStamp);
            var observed = new PassiveTargetObservation(i >= 3, false, clock.GetUtcNow().UtcDateTime,
                i >= 3 ? new EnemySeekVisual(i, i, 2, 2, 4) : null, 2, 2)
            { Source = frame.FrameStamp, BattleId = battle, CaptureEpoch = 1, Quality = CombatObservationQuality.Available };
            CombatDecisionEvidence.Publish(scope, frame, observed);
        }
        CombatInputFeedbackEvidence.End(scope, battle);
        await scope.DisposeAsync();
        var context = saved.Where(item => item.Phase == "host-input-context").ToArray();
        Assert.Single(context.Select(item => item.Window!.WindowId).Distinct());
        Assert.Contains(context, item => item.Window is { Occurrence: "first", RelativeIndex: 0 } && item.Source == feedbackSources[0]);
        var changed = Assert.Single(context.Where(item => item.Window is { Occurrence: "change", RelativeIndex: 0 }));
        Assert.Equal(feedbackSources[3], changed.Source);
        Assert.Contains("health=True", changed.Fields!["anchorFeedbackTarget"]);
        Assert.Contains(context, item => item.Window is { Occurrence: "last", RelativeIndex: 0 } && item.Source == feedbackSources[5]);
        Assert.Equal(6, saved.Count(item => item.Phase == "host-input-feedback"));
    }

    [Theory]
    [InlineData("early")]
    [InlineData("stale")]
    [InlineData("duplicate")]
    [InlineData("epoch")]
    [InlineData("session")]
    [InlineData("battle")]
    [InlineData("unavailable")]
    [InlineData("dimensions")]
    [InlineData("partial-receipt")]
    [InlineData("uncertain")]
    [InlineData("deadline")]
    public async Task FeedbackCannotSubstituteAnUnrelatedFrameOrUnconfirmedInput(string mismatch)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var battle = Guid.NewGuid();
        var input = new CombatBattleHostInput(CombatBattleHostInputKind.Approach)
        { Source = source.Next(), RequestId = Guid.NewGuid(), DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency };
        var receipt = new CombatBattleHostInputResult(CombatBattleHostInputStatus.Sent, clock.GetTimestamp())
        { NativeRequested = 2, NativeSubmitted = mismatch == "partial-receipt" ? 1 : 2, NativeUncertain = mismatch == "uncertain" };
        CombatInputFeedbackEvidence.Arm(scope, battle, 1, input, receipt, clock, NullLogger.Instance);
        clock.Advance(TimeSpan.FromMilliseconds(mismatch == "early" ? 30 : mismatch == "deadline" ? 1100 : 100));
        if (mismatch == "session") source.Restart();
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = mismatch == "duplicate" ? input.Source : source.Next() };
        if (mismatch == "stale") clock.Advance(TimeSpan.FromMilliseconds(200));
        CombatDecisionEvidence.Publish(scope, frame, new(false, false, clock.GetUtcNow().UtcDateTime, null,
            mismatch == "dimensions" ? 3 : 2, 2)
        { Source = frame.FrameStamp, BattleId = mismatch == "battle" ? Guid.NewGuid() : battle,
            CaptureEpoch = mismatch == "epoch" ? 2 : 1,
            Quality = mismatch == "unavailable" ? CombatObservationQuality.Unavailable : CombatObservationQuality.Available });
        CombatInputFeedbackEvidence.End(scope, battle);
        await scope.DisposeAsync();
        Assert.Empty(saved);
        Assert.Equal(0, scope.ExactReservationStatistics.Active);
    }
}
