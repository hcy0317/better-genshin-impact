using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class SeekRecognitionDiagnosticsTests
{
    [Theory]
    [InlineData("single-effect")]
    [InlineData("hud")]
    [InlineData("fixed-hud")]
    [InlineData("crop")]
    public void PartialHintNeverPromotesAnEffectHudOrDerivedViewport(string sample)
    {
        using var frame=NewPortablePartialFrame();
        if(sample=="single-effect")
        {
            Cv2.Rectangle(frame.SrcMat,new Rect(600,500,55,6),new Scalar(40,40,230),-1);
            for(var y=0;y<6;y++)Cv2.Rectangle(frame.SrcMat,new Rect(600+(y%2==0?0:30),500+y,25,1),new Scalar(90,90,255),-1);
        }
        else if(sample=="fixed-hud")
        {
            Cv2.Rectangle(frame.SrcMat,new Rect(900,67,80,6),new Scalar(40,40,230),-1);
            for(var y=1;y<6;y++)Cv2.Rectangle(frame.SrcMat,new Rect(902,67+y,y<=3?73:y==4?69:67,1),new Scalar(90,90,255),-1);
        }
        else PaintPortablePartialBand(frame.SrcMat,600,sample=="hud"?1000:500);
        if(sample=="crop")
        {
            using var original=new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(new Mat(1120,1960,MatType.CV_8UC3,new Scalar(30,70,30)),0,0)
                {FrameStamp=frame.FrameStamp};
            PaintPortablePartialBand(original.SrcMat,620,520);
            using var roi=original.DeriveCrop(new Rect(20,20,1920,1080));roi.FrameStamp=frame.FrameStamp;
            Assert.True(roi.SrcMat.IsSubmatrix());
            Assert.Null(AvatarRecognition.ReadPassiveTargetEvidence(roi,CombatSceneCapabilities.Unknown).Hint);
        }
        else
        {
            var result=AvatarRecognition.ReadPassiveTargetEvidence(frame,CombatSceneCapabilities.Unknown);
            Assert.Null(result.Decision.Cue);Assert.Null(result.Hint);
        }
    }

    private static void PaintPortablePartialBand(Mat source,int x,int y)
    {
        // A connected red band with a salmon right edge that steps left; row0 keeps the original full height.
        Cv2.Rectangle(source,new Rect(x,y,55,6),new Scalar(40,40,230),-1);
        (int First,int Last)[] salmon=[(2,52),(1,51),(0,49),(1,47),(2,46)];
        for(var row=0;row<salmon.Length;row++)
            Cv2.Rectangle(source,new Rect(x+salmon[row].First,y+row+1,salmon[row].Last-salmon[row].First+1,1),new Scalar(90,90,255),-1);
    }

    private static BetterGenshinImpact.GameTask.Model.Area.ImageRegion NewPortablePartialFrame()
    {
        var clock=new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        return new(new Mat(1080,1920,MatType.CV_8UC3,new Scalar(30,70,30)),0,0)
            {FrameStamp=new Fischless.GameCapture.CaptureFrameSource(clock).Next()};
    }

    [Fact]
    public void PartialSalmonBandKeepsNoTargetButOffersOnlyACurrentFullHeightSearchHint()
    {
        using var frame=NewPortablePartialFrame();PaintPortablePartialBand(frame.SrcMat,600,500);
        using var mask=AutoFightSeek.CreateSeekColorMask(frame.SrcMat,new Scalar(255,90,90),null);
        using var labels=new Mat();using var stats=new Mat();using var centroids=new Mat();
        Assert.Equal(2,Cv2.ConnectedComponentsWithStats(mask,labels,stats,centroids,
            connectivity:PixelConnectivity.Connectivity4,ltype:MatType.CV_32S));
        var raw=new EnemySeekVisual(stats.Get<int>(1,0),stats.Get<int>(1,1),stats.Get<int>(1,2),stats.Get<int>(1,3),stats.Get<int>(1,4));
        Assert.Equal(new EnemySeekVisual(600,500,55,6,330),raw);
        // The original classifier's sole feature rejection below proves geometry passed, without private API access.
        Assert.False(AutoFightSeek.MatchesHealthBarFeature(mask,frame.SrcMat,raw));
        var diagnostics=new SeekRecognitionDiagnostics();
        Assert.Null(AutoFightSeek.ClassifySeekVisual(mask,frame.SrcMat,raw,frame.Width,frame.Height,diagnostics,scene:CombatSceneCapabilities.Unknown));
        Assert.Equal(0,diagnostics.HealthWidthRejected);Assert.Equal(0,diagnostics.DarkTrackChecked);
        Assert.Equal(1,diagnostics.Rejections["health:feature/indicator:geometry"]);
        Assert.Single(diagnostics.Rejections);
        // Fixed pixel truth, not a copy of the production core-selection algorithm. No vertical crop.
        var visibleCore=new EnemySeekVisual(602,500,48,6,288);
        Assert.Equal(raw.Height,visibleCore.Height);
        Assert.True(AutoFightSeek.MatchesHealthBarFeature(mask,frame.SrcMat,visibleCore));
        var result=AvatarRecognition.ReadPassiveTargetEvidence(frame,CombatSceneCapabilities.Unknown);
        Assert.Null(result.Decision.Cue);Assert.Equal(0,result.Decision.SignalCount);
        Assert.Null(result.Decision.Visual);Assert.True(frame.FrameStamp.IsKnown);
        Assert.NotNull(result.Hint);
        var hint=result.Hint!.Value;Assert.Equal(frame.FrameStamp,hint.Source);
        Assert.Equal(frame.Width,hint.Width);Assert.Equal(frame.Height,hint.Height);
        Assert.Equal(raw.Height,hint.Visual.Height);
        Assert.InRange(hint.Visual.X,raw.X,raw.X+raw.Width-hint.Visual.Width);
        Assert.Equal(raw.Y,hint.Visual.Y);
    }

    [Fact]
    public void ACompleteHealthTargetWinsOverAPartialBandWithoutCreatingAHint()
    {
        using var frame=NewPortablePartialFrame();PaintPortablePartialBand(frame.SrcMat,600,500);
        Cv2.Rectangle(frame.SrcMat,new Rect(900,500,80,6),new Scalar(90,90,255),-1);
        var result=AvatarRecognition.ReadPassiveTargetEvidence(frame,CombatSceneCapabilities.Unknown);
        Assert.Equal(SeekCueKind.HealthBar,result.Decision.Cue);Assert.Equal(1,result.Decision.SignalCount);
        Assert.Equal(900,result.Decision.Visual!.Value.X);Assert.Null(result.Hint);
    }

    [Fact]
    public void TwoPartialBandsRemainAmbiguousAndNeverBecomeTargetsOrAHint()
    {
        using var frame=NewPortablePartialFrame();PaintPortablePartialBand(frame.SrcMat,600,500);PaintPortablePartialBand(frame.SrcMat,800,650);
        var result=AvatarRecognition.ReadPassiveTargetEvidence(frame,CombatSceneCapabilities.Unknown);
        Assert.Null(result.Decision.Cue);Assert.Equal(0,result.Decision.SignalCount);
        Assert.Null(result.Decision.Visual);Assert.Null(result.Hint);
        UnconfirmedSearchHint[] mixedBodyHints=[new(frame.FrameStamp,new(0,668,23,5,100),1920,1080,UnconfirmedSearchHintKind.ClippedHealthBar),
            new(frame.FrameStamp,new(602,500,48,6,288),1920,1080,UnconfirmedSearchHintKind.PartialHealthBar)];
        Assert.Null(AvatarRecognition.SelectCurrentSearchHint(frame.FrameStamp,1920,1080,false,mixedBodyHints));
    }

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
