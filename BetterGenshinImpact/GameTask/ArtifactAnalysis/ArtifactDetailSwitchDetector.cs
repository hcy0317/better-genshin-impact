using System;
using System.Numerics;
using BetterGenshinImpact.GameTask.Model.GameUI;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal enum ArtifactDetailCaptureDecision
{
    Wait,
    Confirmed,
    TimedOut
}

internal static class ArtifactDetailCapturePolicy
{
    internal const int ConfirmationBudgetMilliseconds = 450;

    internal static ArtifactDetailCaptureDecision Decide(
        int scanIndex,
        long elapsedMilliseconds,
        bool confirmed)
    {
        if (confirmed) return ArtifactDetailCaptureDecision.Confirmed;
        return elapsedMilliseconds >= ConfirmationBudgetMilliseconds
            ? ArtifactDetailCaptureDecision.TimedOut
            : ArtifactDetailCaptureDecision.Wait;
    }
}

internal sealed class ArtifactScanDetailChangeDetector(
    ArtifactPanelSignature initialSignature,
    int maximumStableDistance)
{
    internal bool Observe(ArtifactPanelSignature detailSignature) =>
        detailSignature.DistanceFrom(initialSignature) > maximumStableDistance;
}

internal sealed class ArtifactCharacterDetailSwitchDetector(
    ulong initialSignature,
    int maximumStableDistance)
{
    private bool _changed;
    private ulong _lastSignature = initialSignature;
    private int _stableFrames;

    internal bool Observe(ulong detailSignature)
    {
        if (!_changed)
        {
            if (ArtifactVisualSignature.Distance(
                    detailSignature, initialSignature) > maximumStableDistance)
            {
                _changed = true;
                _lastSignature = detailSignature;
                _stableFrames = 1;
            }
            return false;
        }

        if (ArtifactVisualSignature.Distance(
                detailSignature, _lastSignature) <= maximumStableDistance)
        {
            _stableFrames++;
        }
        else
        {
            _stableFrames = 1;
        }

        _lastSignature = detailSignature;
        return _stableFrames >= 2;
    }
}

internal sealed class ArtifactDetailSwitchDetector(
    ArtifactPanelSignature initialSignature,
    int maximumStableDistance,
    double lockTolerance)
{
    private const int RequiredStableFrames = 2;
    private bool _changed;
    private ArtifactPanelSignature _lastDetailSignature = initialSignature;
    private double _lastLockSignature;
    private int _stableFrames;

    internal bool Observe(
        ArtifactPanelSignature detailSignature,
        double lockSignature)
    {
        if (!_changed)
        {
            if (detailSignature.DistanceFrom(initialSignature) > maximumStableDistance)
            {
                _changed = true;
                _lastDetailSignature = detailSignature;
                _lastLockSignature = lockSignature;
                _stableFrames = 1;
            }
            return false;
        }

        var stable = detailSignature.DistanceFrom(
                         _lastDetailSignature) <= maximumStableDistance &&
                     Math.Abs(lockSignature - _lastLockSignature) <= lockTolerance;
        _stableFrames = stable ? _stableFrames + 1 : 1;
        _lastDetailSignature = detailSignature;
        _lastLockSignature = lockSignature;
        return _stableFrames >= RequiredStableFrames;
    }
}

internal sealed class ArtifactSameDetailSelectionDetector(
    ArtifactPanelSignature initialSignature,
    double baselineSelectionScore,
    double minimumSelectionIncrease,
    int maximumStableDistance = 4)
{
    private const int RequiredStableFrames = 2;
    private int _stableFrames;

    internal bool Observe(
        ArtifactPanelSignature detailSignature,
        double selectionScore)
    {
        var sameDetail = detailSignature.DistanceFrom(initialSignature)
                         <= maximumStableDistance;
        var selectionConfirmed = ArtifactGridSelectionDetector.IsAlreadySelected(
                                     baselineSelectionScore) ||
                                 selectionScore >= baselineSelectionScore + minimumSelectionIncrease;
        _stableFrames = sameDetail && selectionConfirmed
            ? _stableFrames + 1
            : 0;
        return _stableFrames >= RequiredStableFrames;
    }
}

internal sealed class ArtifactCharacterSameDetailSelectionDetector(
    ulong initialSignature,
    double baselineSelectionScore,
    int maximumStableDistance = 2)
{
    private const int RequiredStableFrames = 2;
    private int _stableFrames;

    internal bool Observe(ulong detailSignature)
    {
        var sameDetail = ArtifactVisualSignature.Distance(
                             detailSignature, initialSignature) <= maximumStableDistance;
        _stableFrames = sameDetail && ArtifactGridSelectionDetector.IsAlreadySelected(
            baselineSelectionScore)
            ? _stableFrames + 1
            : 0;
        return _stableFrames >= RequiredStableFrames;
    }
}

internal readonly record struct ArtifactPanelSignature(
    ulong Identity,
    ulong Affixes)
{
    internal int DistanceFrom(ArtifactPanelSignature other) =>
        ArtifactVisualSignature.Distance(Identity, other.Identity) +
        ArtifactVisualSignature.Distance(Affixes, other.Affixes);
}

internal static class ArtifactVisualSignature
{
    internal static ulong Compute(
        Mat capture,
        double left,
        double top,
        double width,
        double height)
    {
        using var detail = ArtifactUiCoordinateMapper.CropNormalized(
            capture, left, top, width, height);
        using var gray = detail.Channels() switch
        {
            4 => detail.CvtColor(ColorConversionCodes.BGRA2GRAY),
            3 => detail.CvtColor(ColorConversionCodes.BGR2GRAY),
            1 => detail.Clone(),
            var channels => throw new InvalidOperationException(
                $"不支持的详情签名截图通道数：{channels}")
        };
        using var reduced = gray.Resize(
            new Size(9, 8), 0, 0, InterpolationFlags.Area);
        ulong signature = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        for (var x = 0; x < 8; x++)
        {
            if (reduced.At<byte>(y, x) > reduced.At<byte>(y, x + 1))
            {
                signature |= 1UL << bit;
            }
            bit++;
        }
        return signature;
    }

    internal static int Distance(ulong left, ulong right) =>
        BitOperations.PopCount(left ^ right);

}

internal static class ArtifactGridSelectionDetector
{
    private const double AlreadySelectedScore = 0.35;

    internal static bool IsAlreadySelected(double score) =>
        score >= AlreadySelectedScore;

    internal static double Score(Mat cell)
    {
        ArgumentNullException.ThrowIfNull(cell);
        if (cell.Empty()) return 0;

        using var gray = cell.Channels() switch
        {
            4 => cell.CvtColor(ColorConversionCodes.BGRA2GRAY),
            3 => cell.CvtColor(ColorConversionCodes.BGR2GRAY),
            1 => cell.Clone(),
            var channels => throw new InvalidOperationException(
                $"不支持的网格选中态截图通道数：{channels}")
        };
        using var bright = gray.Threshold(210, 255, ThresholdTypes.Binary);
        using var borderMask = new Mat(
            cell.Size(), MatType.CV_8UC1, Scalar.Black);
        var borderX = Math.Max(1, cell.Width / 24);
        var borderY = Math.Max(1, cell.Height / 30);
        using (var top = borderMask.SubMat(new Rect(0, 0, cell.Width, borderY)))
            top.SetTo(255);
        using (var left = borderMask.SubMat(new Rect(0, 0, borderX, cell.Height)))
            left.SetTo(255);
        using (var right = borderMask.SubMat(
                   new Rect(cell.Width - borderX, 0, borderX, cell.Height)))
            right.SetTo(255);
        using var selectedBorder = new Mat();
        Cv2.BitwiseAnd(bright, borderMask, selectedBorder);
        return Cv2.CountNonZero(selectedBorder) /
               (double)Math.Max(1, Cv2.CountNonZero(borderMask));
    }
}
