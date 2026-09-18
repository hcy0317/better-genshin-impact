using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class SeekRecognitionDiagnosticsTests
{
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
