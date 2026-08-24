using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.UnitTest.CoreTests.SimulatorTests;

public class SimulationFallbackTests
{
    [Fact]
    public void DispatchWithPostMessageFallback_ShouldNotCallFallbackWhenSendInputSucceeds()
    {
        var primaryCalls = 0;
        var fallbackCalls = 0;

        var usedFallback = Simulation.DispatchWithPostMessageFallback(
            () => primaryCalls++,
            () => fallbackCalls++);

        Assert.False(usedFallback);
        Assert.Equal(1, primaryCalls);
        Assert.Equal(0, fallbackCalls);
    }

    [Fact]
    public void DispatchWithPostMessageFallback_ShouldUseFallbackWhenInputDispatchIsDenied()
    {
        var fallbackCalls = 0;

        var usedFallback = Simulation.DispatchWithPostMessageFallback(
            () => throw new InputDispatchException("sent=0, win32Error=5"),
            () => fallbackCalls++);

        Assert.True(usedFallback);
        Assert.Equal(1, fallbackCalls);
    }

    [Fact]
    public void DispatchWithPostMessageFallback_ShouldNotHideUnrelatedFailures()
    {
        Assert.Throws<InvalidOperationException>(() => Simulation.DispatchWithPostMessageFallback(
            () => throw new InvalidOperationException("unrelated"),
            () => { }));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0x7fff, false)]
    [InlineData(unchecked((short)0x8000), true)]
    [InlineData(unchecked((short)0xffff), true)]
    public void ShouldReleaseInput_ShouldOnlyReleasePressedInputs(short state, bool expected)
    {
        Assert.Equal(expected, Simulation.ShouldReleaseInput(state));
    }

    [Fact]
    public void RegionBackgroundClickTarget_ShouldDispatchCenterWithoutMovingCursor()
    {
        var target = RegionBackgroundClickTarget.Resolve(100, 200, 40, 20);
        var dispatched = (x: -1, y: -1);

        target.Dispatch((x, y) => dispatched = (x, y));

        Assert.Equal((120, 210), dispatched);
    }
}
