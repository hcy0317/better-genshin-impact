using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatLowHpTests
{
    [Fact]
    public void CancelledHealthCapturePropagatesCancellation()
    {
        var evaluator = new ConditionEvaluator(null!, () => throw new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => evaluator.Evaluate("low-hp()", 0));
    }

    [Fact]
    public void UnknownHealthDoesNotBecomeHealthyThroughNegation()
    {
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var evaluator = new ConditionEvaluator(null!, () => throw new InvalidOperationException("must reuse frame"));
        evaluator.SetCachedCapture(frame);
        Assert.Null(Bv.ObserveCurrentAvatarLowHp(frame));
        Assert.False(evaluator.Evaluate("low-hp()", 0));
        Assert.False(evaluator.Evaluate("!low-hp()", 0));
        Assert.False(evaluator.Evaluate("!(low-hp() || false)", 0));
        Assert.True(evaluator.Evaluate("low-hp() || true", 0));
    }

    [Fact]
    public void ObservedGreenHealthIsNotLowAndOutOfBoundsIsUnknown()
    {
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        using (var roi = new Mat(frame.SrcMat, new Rect(805, 1008, 7, 5))) roi.SetTo(new Scalar(100, 210, 150));
        Assert.False(Bv.ObserveCurrentAvatarLowHp(frame));
        Assert.Null(Bv.ObserveCurrentAvatarLowHp(frame, 2));
    }

    [Fact]
    public void OneMissingPixelCannotEraseAnOtherwiseRedHealthSample()
    {
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        using (var roi = new Mat(frame.SrcMat, new Rect(805, 1008, 7, 5))) roi.SetTo(new Scalar(90, 90, 255));
        frame.SrcMat.Set(1010, 808, new Vec3b(0, 0, 0));
        Assert.True(Bv.CurrentAvatarIsLowHp(frame, 1));
    }
}
