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
