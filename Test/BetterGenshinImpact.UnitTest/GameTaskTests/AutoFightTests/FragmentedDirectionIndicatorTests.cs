using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Model.Area;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

[Collection("OfflineNativeDecision")]
public class FragmentedDirectionIndicatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecordedFragmentedArrowStillSelectsLeft(bool indicatorOnly)
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "fragmented-arrow-20260920.png"));
        Assert.False(crop.Empty());
        using (var roi = new Mat(source, new Rect(436, 404, crop.Width, crop.Height))) crop.CopyTo(roi);
        using var frame = new ImageRegion(source.Clone(), 0, 0);
        using var before = AutoFightSeek.CreateSeekColorMask(frame.SrcMat, new Scalar(255, 90, 90), null);
        AutoFightSeek.ResetSeekState();
        var diagnostics = new SeekRecognitionDiagnostics();
        var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
            out _, out _, indicatorOnly, saveDiagnostics: false, diagnostics);
        Assert.NotNull(decision.Visual);
        Assert.Equal(-90d, decision.Visual.Value.IndicatorBearingDegrees);
        Assert.Equal(EnemyIndicatorDirection.Left, decision.Direction);
        Assert.Equal(SeekCueKind.DirectionIndicator, decision.Cue);
        Assert.Equal(1, diagnostics.Accepted);
        using var after = AutoFightSeek.CreateSeekColorMask(frame.SrcMat, new Scalar(255, 90, 90), null);
        Assert.Equal(0, Cv2.Norm(before, after, NormTypes.L1));
        Assert.Equal(0, Cv2.Norm(source, frame.SrcMat, NormTypes.L1));
    }

    [Theory]
    [InlineData(15, 15)]
    [InlineData(18, 21)]
    [InlineData(30, 29)]
    [InlineData(14, 6)]
    [InlineData(40, 2)]
    public void RedBlocksAndLinesDoNotBecomeDirections(int width, int height)
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        Cv2.Rectangle(source, new Rect(446, 414, width, height), new Scalar(100, 70, 255), -1);
        using var frame = new ImageRegion(source.Clone(), 0, 0);
        AutoFightSeek.ResetSeekState();
        var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
            out _, out _, indicatorOnly: true, saveDiagnostics: false);
        Assert.Null(decision.Visual);
    }

    [Theory]
    [InlineData(80, 100)]
    [InlineData(1720, 450)]
    [InlineData(436, 1000)]
    [InlineData(936, 504)]
    [InlineData(1436, 404)]
    public void FragmentedArrowInHudOrWrongScreenDirectionIsRejected(int x, int y)
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "fragmented-arrow-20260920.png"));
        using (var roi = new Mat(source, new Rect(x, y, crop.Width, crop.Height))) crop.CopyTo(roi);
        using var frame = new ImageRegion(source.Clone(), 0, 0);
        AutoFightSeek.ResetSeekState();
        var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
            out _, out _, saveDiagnostics: false);
        Assert.Null(decision.Visual);
    }

    [Fact]
    public void ExistingHealthBarKeepsPriorityOverFragmentedArrow()
    {
        using var source = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", "fragmented-arrow-20260920.png"));
        using (var roi = new Mat(source, new Rect(436, 404, crop.Width, crop.Height))) crop.CopyTo(roi);
        Cv2.Rectangle(source, new Rect(729, 300, 145, 6), new Scalar(90, 90, 255), -1);
        using var frame = new ImageRegion(source.Clone(), 0, 0);
        AutoFightSeek.ResetSeekState();
        var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
            out _, out _, saveDiagnostics: false);
        Assert.Equal(SeekCueKind.HealthBar, decision.Cue);
        Assert.Equal(145, decision.Visual!.Value.Width);
        Assert.Null(decision.Visual.Value.IndicatorBearingDegrees);
    }

    [Theory]
    [InlineData("s32-map-marker-20260916.png")]
    [InlineData("s32-food-revive-20260916.png")]
    public void RecordedNonCombatUiDoesNotGainAnEnemyDirection(string fixture)
    {
        using var source = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Ui", fixture));
        Assert.False(source.Empty());
        using var frame = new ImageRegion(source.Clone(), 0, 0);
        AutoFightSeek.ResetSeekState();
        var decision = AutoFightSeek.RecognizeSeekDecision(frame, new Scalar(255, 90, 90), null,
            out _, out _, indicatorOnly: true, saveDiagnostics: false);
        Assert.Null(decision.Visual);
    }
}
