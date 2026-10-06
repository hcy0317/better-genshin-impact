using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class PartyProbeEvidenceTests
{
    [Fact]
    public async Task InvalidFinalSamplePreservesBothFinalAndLastAdmittedSources()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var fence = new CaptureFrameFence(producer.Next(), clock.GetTimestamp());
        var request = Guid.NewGuid();
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        clock.Advance(TimeSpan.FromMilliseconds(10));
        using var admitted = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = producer.Next() };
        var sample = new PartySetupFinishObservation(admitted.FrameStamp.Sequence, admitted.FrameStamp.CapturedAt, 1920, 1080, false, 0)
        { Source = admitted.FrameStamp };
        NativeCombatBattleHostIo.CapturePartyProbe(admitted, sample, true, request, fence, clock, NullLogger.Instance, Guid.NewGuid());
        using var final = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = producer.Next() };
        clock.Advance(TimeSpan.FromSeconds(1));
        NativeCombatBattleHostIo.CapturePartyProbe(final, sample with { Source = final.FrameStamp }, true, request, fence, clock, NullLogger.Instance, Guid.NewGuid());
        NativeCombatBattleHostIo.CapturePartyTerminal(request, final.FrameStamp, admitted.FrameStamp, "stop after cleanup", NullLogger.Instance);
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Phase == "party-terminal" && item.Source == final.FrameStamp);
        Assert.Contains(saved, item => item.Phase == "party-last-admitted" && item.Source == admitted.FrameStamp);
    }
    [Fact]
    public async Task InvalidFirstSampleDoesNotHideTheLastAdmittedNegativeSample()
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var before = source.Next();
        var fence = new CaptureFrameFence(before, clock.GetTimestamp());
        var request = Guid.NewGuid();
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        CaptureFrameStamp last = default;
        for (var index = 0; index < 3; index++)
        {
            if (index > 0) clock.Advance(TimeSpan.FromMilliseconds(10));
            using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
            { FrameStamp = last = source.Next() };
            var sample = new PartySetupFinishObservation(last.Sequence, last.CapturedAt, 1920, 1080, false, 0) { Source = last };
            NativeCombatBattleHostIo.CapturePartyProbe(frame, sample, true, request, fence, clock, NullLogger.Instance, Guid.NewGuid());
        }
        Assert.True(scope.CaptureExactWindow("party-probe", request.ToString("N"), last, "party-terminal", "exact final source"));
        scope.ForgetExactFrame("party-probe", request.ToString("N"));
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Phase == "post-input-invalid-source");
        Assert.Contains(saved, item => item.Phase == "bar-not-visible");
        Assert.Contains(saved, item => item.Phase == "party-terminal" && item.Source == last);
    }
}
