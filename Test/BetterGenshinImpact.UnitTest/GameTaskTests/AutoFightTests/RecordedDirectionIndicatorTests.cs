using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Model.Area;
using Newtonsoft.Json;
using OpenCvSharp;
using Xunit.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

[Collection("OfflineNativeDecision")]
public class RecordedDirectionIndicatorTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(1280, 720, false)]
    [InlineData(1920, 1080, false)]
    [InlineData(2560, 1440, false)]
    [InlineData(1280, 720, true)]
    [InlineData(1920, 1080, true)]
    [InlineData(2560, 1440, true)]
    public void CommissionArrowAgainstFireRetainsItsDirection(int width, int height, bool mirror)
    {
        using var frame = CreateFrame(width, height, mirror);
        var decision = Recognize(frame);
        Assert.Equal(SeekCueKind.DirectionIndicator, decision.Cue);
        Assert.Equal(mirror ? EnemyIndicatorDirection.Right : EnemyIndicatorDirection.Left, decision.Direction);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ArrowShapeCannotOverrideContradictoryScreenPosition(bool mirror)
    {
        using var frame = CreateFrame(1920, 1080, mirror, wrongSide: true);
        Assert.NotEqual(SeekCueKind.DirectionIndicator, Recognize(frame).Cue);
    }

    [UiRecoveryFact("commission-arrow-full-20261026.png")]
    public void OriginalCommissionFailureFramePublishesTheLeftEnemyCue()
    {
        using var pixels = Cv2.ImRead(UiRecoveryFixtures.PathFor("commission-arrow-full-20261026.png"));
        Assert.False(pixels.Empty());
        using var frame = new ImageRegion(pixels.Clone(), 0, 0);
        var decision = Recognize(frame);
        Assert.Equal(SeekCueKind.DirectionIndicator, decision.Cue);
        Assert.Equal(EnemyIndicatorDirection.Left, decision.Direction);
        Assert.InRange(decision.Visual!.Value.X, 445, 460);
    }

    private EnemySeekDecision Recognize(ImageRegion frame)
    {
        var diagnostics = new SeekRecognitionDiagnostics();
        AutoFightSeek.ResetSeekState();
        var hints = new List<EnemySeekVisual>();
        var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
            out _, out _, saveDiagnostics: false, diagnostics: diagnostics, unconfirmedDirection: hints.Add);
        using var mask = AutoFightSeek.CreateSeekColorMask(frame.SrcMat, new Scalar(255, 90, 90), null);
        var bearings = hints.Take(4).Select(hint =>
        {
            using var candidate = new Mat(mask, new Rect(hint.X, hint.Y, hint.Width, hint.Height));
            var geometric = AutoFightSeek.TryGetDirectionIndicatorContourBearing(candidate, out var bearing);
            var contour = AutoFightSeek.GetLargestExternalContour(candidate)!;
            var hull = Cv2.ConvexHull(contour);
            var shape = Cv2.Moments(contour);
            var envelope = Cv2.Moments(hull);
            return new { hint, geometric, bearing, shapeArea = shape.M00, hullArea = envelope.M00,
                dx = shape.M10 / shape.M00 - envelope.M10 / envelope.M00,
                dy = shape.M01 / shape.M00 - envelope.M01 / envelope.M00,
                screen = AutoFightSeek.GetIndicatorScreenBearingDegrees(hint, frame.Width, frame.Height) };
        }).ToArray();
        output.WriteLine(JsonConvert.SerializeObject(new { decision, diagnostics, bearings }));
        return decision;
    }

    private static ImageRegion CreateFrame(int width, int height, bool mirror, bool wrongSide = false)
    {
        // 2026-10-26 04:18:55 commission failure, original crop (420,590,140,100).
        // Only the indicator and its immediate background are retained; no account HUD.
        using var original = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Combat", "commission-arrow-left-20261026.png"));
        Assert.False(original.Empty());
        using var crop = new Mat();
        if (mirror) Cv2.Flip(original, crop, FlipMode.Y); else original.CopyTo(crop);
        using var resized = new Mat();
        Cv2.Resize(crop, resized, new Size(140 * width / 1920, 100 * height / 1080));
        using var pixels = new Mat(height, width, MatType.CV_8UC3, Scalar.Black);
        var x = mirror != wrongSide ? 1360 : 420;
        using (var destination = new Mat(pixels, new Rect(x * width / 1920, 590 * height / 1080, resized.Width, resized.Height)))
            resized.CopyTo(destination);
        return new ImageRegion(pixels.Clone(), 0, 0);
    }
}
