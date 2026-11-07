using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class SeekRecognitionDiagnosticsTests
{
    [Fact]
    public void UniqueCurrentClipWinsOverTheRecordedUnconfirmedDirectionNoise()
    {
        var source=new Fischless.GameCapture.CaptureFrameSource(new Microsoft.Extensions.Time.Testing.FakeTimeProvider()).Next();
        var clip=new UnconfirmedSearchHint(source,new(0,668,23,7,161),1920,1080,UnconfirmedSearchHintKind.ClippedHealthBar);
        UnconfirmedSearchHint[] candidates=[clip,new(source,new(465,313,19,23,294),1920,1080),
            new(source,new(1210,911,22,30,299),1920,1080),new(source,new(1210,911,22,32,319),1920,1080)];
        var selected=AvatarRecognition.SelectCurrentSearchHint(source,1920,1080,false,candidates);
        Assert.Equal(clip,selected);
        Assert.Equal(UnconfirmedSearchHintKind.ClippedHealthBar,selected!.Value.Kind);
    }

    [Fact]
    public void MultipleCurrentClipsRemainAmbiguousEvenWithOneDirectionHint()
    {
        var source=new Fischless.GameCapture.CaptureFrameSource(new Microsoft.Extensions.Time.Testing.FakeTimeProvider()).Next();
        UnconfirmedSearchHint[] candidates=[new(source,new(0,500,20,5,100),1920,1080,UnconfirmedSearchHintKind.ClippedHealthBar),
            new(source,new(0,650,20,5,100),1920,1080,UnconfirmedSearchHintKind.ClippedHealthBar),
            new(source,new(465,313,19,23,294),1920,1080)];
        Assert.Null(AvatarRecognition.SelectCurrentSearchHint(source,1920,1080,false,candidates));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoClipPreservesTheOriginalUniqueDirectionRule(bool multiple)
    {
        var source=new Fischless.GameCapture.CaptureFrameSource(new Microsoft.Extensions.Time.Testing.FakeTimeProvider()).Next();
        var first=new UnconfirmedSearchHint(source,new(465,313,19,23,294),1920,1080);
        var candidates=new List<UnconfirmedSearchHint>{first};
        if(multiple)candidates.Add(new(source,new(1210,911,22,30,299),1920,1080));
        var selected=AvatarRecognition.SelectCurrentSearchHint(source,1920,1080,false,candidates);
        if(multiple)Assert.Null(selected);else Assert.Equal(first,selected);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("unknown")]
    [InlineData("foreign")]
    [InlineData("dimensions")]
    public void PackingNeverUsesClipPriorityToBypassTheOriginalSourceOrTargetContract(string invalid)
    {
        var source=new Fischless.GameCapture.CaptureFrameSource(new Microsoft.Extensions.Time.Testing.FakeTimeProvider()).Next();
        var clip=new UnconfirmedSearchHint(invalid=="foreign"?default:source,new(0,668,23,7,161),
            invalid=="dimensions"?960:1920,1080,UnconfirmedSearchHintKind.ClippedHealthBar);
        Assert.Null(AvatarRecognition.SelectCurrentSearchHint(invalid=="unknown"?default:source,1920,1080,invalid=="target",[clip]));
    }

    [Theory]
    [InlineData("left")]
    [InlineData("interior")]
    [InlineData("right-hud")]
    [InlineData("bottom-hud")]
    [InlineData("top-hud")]
    [InlineData("flower")]
    [InlineData("non-bar")]
    [InlineData("multiple")]
    [InlineData("unknown-source")]
    [InlineData("derived-roi")]
    public void OnlyAUniqueCurrentLeftViewportClippedBarOffersCameraOnlyHint(string sample)
    {
        var clock=new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var frame=new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(new Mat(1080,1920,MatType.CV_8UC3,Scalar.Black),0,0)
            { FrameStamp=sample=="unknown-source"?default:new Fischless.GameCapture.CaptureFrameSource(clock).Next() };
        var x=sample=="right-hud"?1900:sample is "interior" or "derived-roi"?500:0;
        var y=sample=="bottom-hud"?1000:sample=="top-hud"?100:500;
        if(sample=="flower") Cv2.Circle(frame.SrcMat,new Point(0,500),12,new Scalar(90,90,255),-1);
        else Cv2.Rectangle(frame.SrcMat,new Rect(x,y,sample=="non-bar"?4:20,sample=="non-bar"?20:5),new Scalar(90,90,255),-1);
        if(sample=="multiple") Cv2.Rectangle(frame.SrcMat,new Rect(0,650,20,5),new Scalar(90,90,255),-1);
        if(sample=="derived-roi")
        {
            using var roi=frame.DeriveCrop(new Rect(500,450,100,100)); roi.FrameStamp=frame.FrameStamp;
            Assert.Null(AvatarRecognition.ReadPassiveTargetEvidence(roi).Hint);
            return;
        }
        var result=AvatarRecognition.ReadPassiveTargetEvidence(frame);
        Assert.Null(result.Decision.Cue);
        if(sample=="left")
        {
            var hint=Assert.IsType<UnconfirmedSearchHint>(result.Hint);
            Assert.Equal(UnconfirmedSearchHintKind.ClippedHealthBar,hint.Kind);
            Assert.Equal(frame.FrameStamp,hint.Source); Assert.Equal(0,hint.Visual.X);
            Assert.Equal(frame.Width,hint.Width); Assert.Equal(frame.Height,hint.Height);
        }
        else Assert.Null(result.Hint);
    }

    [Fact]
    public void AConfirmedFullHealthTargetHasPriorityOverAClippedCandidate()
    {
        var clock=new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        using var frame=new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(new Mat(1080,1920,MatType.CV_8UC3,Scalar.Black),0,0)
            { FrameStamp=new Fischless.GameCapture.CaptureFrameSource(clock).Next() };
        Cv2.Rectangle(frame.SrcMat,new Rect(0,500,20,5),new Scalar(90,90,255),-1);
        Cv2.Rectangle(frame.SrcMat,new Rect(800,500,80,6),new Scalar(90,90,255),-1);
        var result=AvatarRecognition.ReadPassiveTargetEvidence(frame);
        Assert.Equal(SeekCueKind.HealthBar,result.Decision.Cue);
        Assert.Null(result.Hint);
    }

    [Theory]
    [InlineData(DamageNumberRecognitionMode.Disabled, false, "not-run:Disabled")]
    [InlineData(DamageNumberRecognitionMode.Color, false, "not-found:Color")]
    [InlineData(DamageNumberRecognitionMode.Ocr, true, "found:Ocr")]
    public void DisabledRecognitionIsNotReportedAsANegativeObservation(DamageNumberRecognitionMode mode, bool found, string expected)
        => Assert.Equal(expected, AvatarRecognition.DescribeDamageFallback(mode, found));

    [Theory]
    [InlineData(-1, "future-observation")]
    [InlineData(251, "passive-observation-older-than-250ms")]
    [InlineData(50, "all-visual-candidates-filtered")]
    public void AbsentTargetDiagnosticsSeparateRejectedFramesFromRejectedCandidates(int ageMs, string expected)
    {
        var captured = new DateTime(2026, 9, 18, 8, 14, 59, DateTimeKind.Utc);
        var observation = new PassiveTargetObservation(false, false, captured, null, 1920, 1080)
        { TargetAbsenceReason = "all-visual-candidates-filtered" };
        Assert.False(AutoFightSeek.TryCreatePassiveDecision(observation, captured.AddMilliseconds(ageMs),
            out _, out _, out _, out var reason));
        Assert.Equal(expected, reason);
    }

    [Theory]
    [InlineData(14)]
    [InlineData(11)]
    public void NarrowRedBarReportsTheGeometryRejectionWithoutChangingClassification(int width)
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        var rect = new Rect(741, 147, width, 6);
        Cv2.Rectangle(source, rect, new Scalar(90, 90, 255), -1);
        using var mask = AutoFightSeek.CreateSeekColorMask(source, new Scalar(255, 90, 90), null);
        var visual = new EnemySeekVisual(rect.X, rect.Y, rect.Width, rect.Height, width * 6);
        Assert.True(AutoFightSeek.MatchesHealthBarFeature(mask, source, visual));
        var withoutDiagnostics = AutoFightSeek.ClassifySeekVisual(mask, source, visual, 1920, 1080);
        var diagnostics = new SeekRecognitionDiagnostics();
        var withDiagnostics = AutoFightSeek.ClassifySeekVisual(mask, source, visual, 1920, 1080, diagnostics);
        Assert.Null(withoutDiagnostics);
        Assert.Equal(withoutDiagnostics, withDiagnostics);
        Assert.Equal(1, diagnostics.HealthWidthRejected);
        Assert.Equal(24, Assert.Single(diagnostics.NarrowBarSamples).MinimumWidth);
    }
}
