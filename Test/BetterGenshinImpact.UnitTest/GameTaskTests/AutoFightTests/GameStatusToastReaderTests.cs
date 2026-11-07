using System;
using System.IO;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Fischless.GameCapture;
using OpenCvSharp;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public sealed class GameStatusToastReaderTests
{
    // The native image and templates remain real. Text is an external fake boundary, not model output.
    [Theory]
    [InlineData("当前状态不可进行队伍配置", false, true)]
    [InlineData("战斗中无法", false, true)]
    [InlineData("无法切换目标角色", true, false)]
    [InlineData("无 法 切换 目标角色", true, false)]
    [InlineData("当前 状态不可进行队伍配置", false, false)]
    [InlineData("战斗 中无法", false, false)]
    [InlineData("战斗中无", false, false)]
    [InlineData("", false, false)]
    public void NativeWorldUsesThePreparedLineReaderForItsPartyRejectionEvidence(string text,bool selection,bool party)
    {
        Assert.True(ApplicationHostBootstrapGuard.IsProhibited);
        Assert.Null(System.Windows.Application.Current);
        using var frame = RecordedWorld();
        using var ocr = new PreparedLineOcr(frame.Width,frame.Height,text);
        var revive = Detector(frame,ocr);
        UiSnapshot? observed = null;
        var error = Record.Exception(() => observed = NativeUiDriver.ReadNativeScene(frame,inspectWorld:true,ocr:ocr,reviveDetector:revive));
        Assert.True(error == null, $"wideCalls={ocr.WideCalls} toastCalls={ocr.ToastCalls} error={error}");
        Assert.True(observed!.MainReady);
        Assert.Equal(party,observed.World!.Value.PartyRejected);
        Assert.Equal(selection ? SelectionRefusalCue.GameRefusedSelection : SelectionRefusalCue.Absent,
            CombatMotionReader.ReadSelectionRefusal(frame,ocr));
        Assert.Equal(0,ocr.WideCalls); Assert.Equal(1,ocr.ToastCalls);
        var evidence=GameStatusToastReader.Read(frame,ocr);
        Assert.Equal(frame.FrameStamp,evidence.Source);
        Assert.Equal(selection,evidence.SelectionRefused); Assert.Equal(party,evidence.PartyRejected);
        Assert.Equal(1,ocr.ToastCalls);
        Assert.False(ocr.Disposed); Assert.False(frame.SrcMat.IsDisposed);
        Assert.Null(System.Windows.Application.Current);
    }

    [Fact]
    public void NativeNotReadyPropagatesWhileCombatReturnsUnknownAndNeitherCachesAbsent()
    {
        using var frame=RecordedWorld();
        using var ocr=new PreparedLineOcr(frame.Width,frame.Height,"无法切换目标角色");
        var failure=new RecognitionNotReadyException("explicit fake service is not ready");
        ocr.ToastFailure=failure;
        Assert.Same(failure,Assert.Throws<RecognitionNotReadyException>(()=>NativeUiDriver.ReadNativeScene(frame,inspectWorld:true,ocr:ocr,reviveDetector:Detector(frame,ocr))));
        Assert.Equal(SelectionRefusalCue.Unknown,CombatMotionReader.ReadSelectionRefusal(frame,ocr));
        Assert.Equal(2,ocr.ToastCalls);
        ocr.ToastFailure=null;
        Assert.Equal(SelectionRefusalCue.GameRefusedSelection,CombatMotionReader.ReadSelectionRefusal(frame,ocr));
        Assert.Equal(3,ocr.ToastCalls); Assert.False(ocr.Disposed);
    }

    [Fact]
    public void TheOriginalNativeCallerAlsoPropagatesOtherReadErrors()
    {
        using var frame=RecordedWorld();
        using var ocr=new PreparedLineOcr(frame.Width,frame.Height,"");
        var failure=new IOException("external fake read failure");ocr.ToastFailure=failure;
        Assert.Same(failure,Assert.Throws<IOException>(()=>NativeUiDriver.ReadNativeScene(frame,inspectWorld:true,ocr:ocr,reviveDetector:Detector(frame,ocr))));
        Assert.False(ocr.Disposed);Assert.False(frame.SrcMat.IsDisposed);
    }

    [Fact]
    public void SameFrameReuseRequiresTheSamePreparedServiceIdentityEvenIfServicesCompareEqual()
    {
        using var frame=PlainFrame();
        using var role=new PreparedLineOcr(1920,1080,"无法切换目标角色");
        using var party=new PreparedLineOcr(1920,1080,"战斗中无法");
        Assert.True(role.Equals(party));
        var first=GameStatusToastReader.Read(frame,role);
        Assert.Equal(first,GameStatusToastReader.Read(frame,role));
        var other=GameStatusToastReader.Read(frame,party);
        Assert.True(first.SelectionRefused);Assert.False(first.PartyRejected);
        Assert.False(other.SelectionRefused);Assert.True(other.PartyRejected);
        Assert.Equal(1,role.ToastCalls);Assert.Equal(1,party.ToastCalls);
        Assert.Equal(frame.FrameStamp,first.Source);Assert.Equal(frame.FrameStamp,other.Source);
        Assert.False(role.Disposed);Assert.False(party.Disposed);
    }

    [Fact]
    public void NewAndDerivedRegionsDoNotInheritThePreviousRegionObservation()
    {
        using var first=PlainFrame();using var next=PlainFrame();
        using var derived=first.DeriveCrop(new Rect(0,0,1920,1080));
        using var ocr=new PreparedLineOcr(1920,1080,"战斗中无法");
        Assert.True(GameStatusToastReader.Read(first,ocr).PartyRejected);
        Assert.True(GameStatusToastReader.Read(next,ocr).PartyRejected);
        Assert.True(GameStatusToastReader.Read(derived,ocr).PartyRejected);
        Assert.Equal(3,ocr.ToastCalls);Assert.NotEqual(first.FrameStamp,next.FrameStamp);
        Assert.False(first.SrcMat.IsDisposed);Assert.False(next.SrcMat.IsDisposed);
    }

    [Fact]
    public void ACompletedAbsentObservationIsReusedButAnExceptionIsNot()
    {
        using var frame=PlainFrame();using var ocr=new PreparedLineOcr(1920,1080,"");
        var failed=new IOException("first external attempt");ocr.ToastFailure=failed;
        Assert.Same(failed,Assert.Throws<IOException>(()=>GameStatusToastReader.Read(frame,ocr)));
        ocr.ToastFailure=null;
        var absent=GameStatusToastReader.Read(frame,ocr);
        Assert.False(absent.SelectionRefused);Assert.False(absent.PartyRejected);
        Assert.Equal(absent,GameStatusToastReader.Read(frame,ocr));
        Assert.Equal(2,ocr.ToastCalls);Assert.False(ocr.Disposed);
    }

    private static ImageRegion RecordedWorld() => new(Cv2.ImRead(Path.Combine(AppContext.BaseDirectory,"Fixtures","Ui","s32-mining-hud-20260916.png")),0,0)
        {FrameStamp=new CaptureFrameSource(TimeProvider.System).Next()};
    private static ImageRegion PlainFrame() => new(new Mat(1080,1920,MatType.CV_8UC3,new Scalar(13,29,47)),0,0)
        {FrameStamp=new CaptureFrameSource(TimeProvider.System).Next()};
    private static ReviveUiDetector Detector(ImageRegion frame,IOcrService ocr) => new(RecognitionAssets.Get("AutoFight","Confirm",frame),ocr,"复苏","使用道具复苏角色");

    private sealed class PreparedLineOcr(int width,int height,string text) : IOcrService,IDisposable
    {
        internal int WideCalls,ToastCalls;
        internal bool Disposed;
        internal Exception? ToastFailure;
        public string Ocr(Mat image) => string.Empty;
        public OcrResult OcrResult(Mat image)
        {
            if (Math.Abs(image.Cols-width*.7)<2 && Math.Abs(image.Rows-height*.53)<2)
            {WideCalls++;throw new InvalidOperationException("The original broad world-state detector is forbidden at this explicit line-only OCR boundary.");}
            return new OcrResult([]);
        }
        public string OcrWithoutDetector(Mat image)
        {
            if (image.Cols==(int)(width*.40) && image.Rows==(int)(height*.055))
            {ToastCalls++;if(ToastFailure!=null)throw ToastFailure;return text;}
            return string.Empty;
        }
        public override bool Equals(object? other) => other is PreparedLineOcr;
        public override int GetHashCode() => 1;
        public void Dispose() => Disposed=true;
    }
}
