using BetterGenshinImpact.GameTask.AutoTrackPath;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class MapTargetClickAdmissionTests
{
    [Fact]
    public void MissingIconRetainsUnavailableTargetPolicyWithoutClaimingNotActivated()
    {
        var error = new TeleportTargetLocalizationException("目标传送图标不可见");
        Assert.True(PathingTargetUnavailableException.IsUnavailableTarget(error));
        Assert.IsNotType<BetterGenshinImpact.GameTask.Common.Exceptions.TpPointNotActivate>(error);
        Assert.False(PathingTargetUnavailableException.IsUnavailableTarget(new OperationCanceledException()));
    }
    [Fact]
    public void RegistrationOfNeighborsDoesNotAuthorizeMissingTarget()
    {
        Assert.False(MapTargetClickAdmission.Accepts(false, 1022, 428, null, [new(1010, 415, 24, 28)]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmptyGroundCannotBeClicked(bool force)
    {
        Assert.False(MapTargetClickAdmission.Accepts(force, 1022, 428, new Rect(1040, 415, 24, 28),
            [new(1040, 415, 24, 28)]));
    }

    [Fact]
    public void OrdinaryTargetMustUseItsOwnMatchedIcon()
    {
        Assert.True(MapTargetClickAdmission.Accepts(false, 1022, 428, new Rect(1010, 415, 24, 28), []));
        Assert.False(MapTargetClickAdmission.Accepts(false, 1022, 428, new Rect(1050, 415, 24, 28),
            [new(1010, 415, 24, 28)]));
    }

    [Fact]
    public void ForceValidatesOriginalCoordinateWithoutSnapping()
    {
        const double x = 1022.125, y = 428.5;
        Assert.True(MapTargetClickAdmission.Accepts(true, x, y, null, [new(1010, 415, 24, 28)]));
        Assert.False(MapTargetClickAdmission.Accepts(true, x, y, null, [new(1023, 415, 24, 28)]));
    }
}
