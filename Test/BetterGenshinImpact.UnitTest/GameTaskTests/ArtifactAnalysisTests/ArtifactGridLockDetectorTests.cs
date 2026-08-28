using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactGridLockDetectorTests
{
    [Fact]
    public void CoralMarkerAtTheYasGridPositionMeansLocked()
    {
        using var cell = new Mat(126, 102, MatType.CV_8UC3, Scalar.White);
        cell.Set(14, 12, new Vec3b(117, 138, 255));

        Assert.True(ArtifactGridLockDetector.IsLocked(cell));
    }

    [Fact]
    public void GrayMarkerAtTheYasGridPositionMeansUnlocked()
    {
        using var cell = new Mat(126, 102, MatType.CV_8UC3, Scalar.White);
        cell.Set(14, 12, new Vec3b(150, 150, 150));

        Assert.False(ArtifactGridLockDetector.IsLocked(cell));
    }

    [Fact]
    public void CoralOutsideTheYasProbeDoesNotAffectLockState()
    {
        using var cell = new Mat(126, 102, MatType.CV_8UC3, Scalar.White);
        cell.Set(100, 80, new Vec3b(117, 138, 255));

        Assert.False(ArtifactGridLockDetector.IsLocked(cell));
    }
}
