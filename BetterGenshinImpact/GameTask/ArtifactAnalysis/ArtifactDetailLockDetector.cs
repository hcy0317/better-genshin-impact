using OpenCvSharp;
using BetterGenshinImpact.GameTask.Model.GameUI;
using System;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static class ArtifactDetailLockDetector
{
    internal static Point ButtonCenter(Size captureSize) =>
        ArtifactUiCoordinateMapper.ToCapturePoint(captureSize, 1415, 357);

    internal static bool IsLocked(Mat capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Empty() || capture.Channels() < 3) return false;

        var probe = ProbeBounds(capture.Size());
        using var button = capture.SubMat(probe);
        using var coralMask = new Mat();
        Cv2.InRange(
            button,
            new Scalar(80, 80, 210),
            new Scalar(180, 190, 255),
            coralMask);
        return Cv2.CountNonZero(coralMask) >= 4;
    }

    internal static double VisualSignature(Mat capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (capture.Empty() || capture.Channels() < 3) return 0;

        using var button = capture.SubMat(ProbeBounds(capture.Size()));
        var mean = Cv2.Mean(button);
        return mean.Val0 + mean.Val1 + mean.Val2;
    }

    private static Rect ProbeBounds(Size captureSize)
    {
        return ArtifactUiCoordinateMapper.ToCaptureRect(
            captureSize, 1400, 350, 30, 40);
    }
}

internal sealed class ArtifactDetailLockTransitionDetector(
    bool initialLocked,
    bool desiredLocked,
    double initialVisualSignature,
    double tolerance)
{
    private bool _lastLocked = initialLocked;
    private double _lastVisualSignature = initialVisualSignature;
    private int _stableFrames;

    internal ArtifactDetailTransitionOutcome Observe(
        bool locked,
        double visualSignature)
    {
        var stable = locked == _lastLocked &&
                     Math.Abs(visualSignature - _lastVisualSignature) <= tolerance;
        _stableFrames = stable ? _stableFrames + 1 : 1;
        _lastLocked = locked;
        _lastVisualSignature = visualSignature;

        if (locked == desiredLocked &&
            ArtifactLockExecutionPolicy.IsStableDetailState(_stableFrames))
        {
            return ArtifactDetailTransitionOutcome.DesiredStable;
        }
        if (locked == initialLocked &&
            ArtifactLockExecutionPolicy.CanTreatAsStableUnchanged(_stableFrames))
        {
            return ArtifactDetailTransitionOutcome.UnchangedStable;
        }
        return ArtifactDetailTransitionOutcome.Unstable;
    }
}
