using BetterGenshinImpact.Core.Simulator;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class PostMessageMouseWheelTests
{
    [Theory]
    [InlineData(-2, -240)]
    [InlineData(1, 120)]
    public void MakeMouseWheelWParam_UsesSignedWindowsWheelDelta(
        int clicks,
        short expectedDelta)
    {
        var value = PostMessageSimulator.MakeMouseWheelWParam(clicks).ToInt64();
        var delta = unchecked((short)((value >> 16) & 0xffff));

        Assert.Equal(expectedDelta, delta);
    }
}
