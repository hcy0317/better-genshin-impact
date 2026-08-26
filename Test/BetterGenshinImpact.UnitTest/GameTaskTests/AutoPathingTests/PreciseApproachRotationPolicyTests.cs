using BetterGenshinImpact.GameTask.AutoPathing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PreciseApproachRotationPolicyTests
{
    [Fact]
    public void Observe_ShouldAbortAfterTwoConsecutiveRotationFailures()
    {
        var policy = new PreciseApproachRotationPolicy(maxConsecutiveFailures: 2);

        Assert.False(policy.Observe(rotated: false));
        Assert.True(policy.Observe(rotated: false));
    }

    [Fact]
    public void Observe_ShouldResetFailureCountAfterSuccessfulRotation()
    {
        var policy = new PreciseApproachRotationPolicy(maxConsecutiveFailures: 2);

        Assert.False(policy.Observe(rotated: false));
        Assert.False(policy.Observe(rotated: true));
        Assert.False(policy.Observe(rotated: false));
    }
}
