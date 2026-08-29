using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class TeleportZoomToleranceTests
{
    [Theory]
    [InlineData(4.47, 4.4, 0, true)]
    [InlineData(4.50, 4.4, 0, true)]
    [InlineData(4.51, 4.4, 0, false)]
    [InlineData(4.55, 4.4, 0.05, true)]
    public void DisplayZoomAllowsOneMeasurementStepOfTolerance(
        double measured,
        double required,
        double precision,
        bool expected)
    {
        Assert.Equal(
            expected,
            TpTask.IsTeleportPointDisplayZoomLevelReached(measured, required, precision));
    }
}
