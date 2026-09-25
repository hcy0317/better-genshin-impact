using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

[Collection("OfflineNativeDecision")]
public class RecordedS63SearchHintTests
{
    [OfflineNativeDecisionFact]
    public void MultipleUnconfirmedCandidatesCannotChooseAnArbitrarySearchHint()
    {
        var path = Environment.GetEnvironmentVariable("BGI_S63_HINT_FRAME");
        Assert.True(File.Exists(path));
        using var pixels = Cv2.ImRead(path!);
        using (var source = new Mat(pixels, new Rect(550, 767, 46, 40)))
        using (var destination = new Mat(pixels, new Rect(440, 767, 46, 40)))
            source.CopyTo(destination);
        using var frame = new ImageRegion(pixels.Clone(), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
        var read = AvatarRecognition.ReadPassiveTargetEvidence(frame);
        Assert.Null(read.Decision.Visual);
        Assert.Null(read.Hint);
    }

    [OfflineNativeDecisionFact]
    public void RecordedUnknownBearingRemainsNonTargetButCarriesBoundedSearchEvidence()
    {
        var path = Environment.GetEnvironmentVariable("BGI_S63_HINT_FRAME");
        Assert.True(File.Exists(path));
        using var frame = new ImageRegion(Cv2.ImRead(path!), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
        var read = AvatarRecognition.ReadPassiveTargetEvidence(frame);
        Assert.Null(read.Decision.Visual);
        Assert.Equal(AutoFightSeekAction.Scan, read.Decision.Action);
        Assert.NotNull(read.Hint);
        Assert.Equal(frame.FrameStamp, read.Hint.Value.Source);
        Assert.Equal(558, read.Hint.Value.Visual.X);
        Assert.Equal(775, read.Hint.Value.Visual.Y);
        Assert.Null(read.Hint.Value.Visual.IndicatorBearingDegrees);
        var passive = new PassiveTargetObservation(false, false, frame.FrameStamp.CapturedAt.UtcDateTime,
            null, frame.Width, frame.Height)
        { Source = frame.FrameStamp, BattleId = Guid.NewGuid(), CaptureEpoch = 7,
            Quality = CombatObservationQuality.Available, SearchHint = read.Hint };
        var native = NativeCombatBattleHostIo.ProjectObservation(passive, 7, true, frame.FrameStamp.CapturedAt.UtcDateTime);
        Assert.Null(native.Target);
        Assert.Equal(read.Hint, native.SearchHint);
        Assert.Equal(CombatObservationQuality.Available, native.Quality);
        Assert.Equal(CombatObservationQuality.Unavailable,
            NativeCombatBattleHostIo.ProjectObservation(passive, 8, true, frame.FrameStamp.CapturedAt.UtcDateTime).Quality);
        Assert.Equal(CombatObservationQuality.Unavailable,
            NativeCombatBattleHostIo.ProjectObservation(passive, 7, false, frame.FrameStamp.CapturedAt.UtcDateTime).Quality);
    }
}
