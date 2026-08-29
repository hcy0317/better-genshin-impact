using BetterGenshinImpact.GameTask.Common.Job;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class ScanPickTaskTests
{
    [Fact]
    public void SortPickItems_ShouldPreferGroundedCenterBeam()
    {
        var farRightBeam = new Rect(1557, 608, 16, 109);
        var centerBeam = new Rect(1124, 632, 43, 164);
        var upperParticle = new Rect(1016, 502, 25, 60);

        var result = ScanPickTask.SortPickItems([farRightBeam, centerBeam, upperParticle], 1920, 1080).ToList();

        Assert.Equal(centerBeam, result[0]);
    }

    [Fact]
    public void GetMovementDecision_ShouldScaleFrom1080PThresholds()
    {
        var decision1080P = ScanPickTask.GetMovementDecision(new Rect(1124, 632, 43, 164), 1920, 1080);
        var decision900P = ScanPickTask.GetMovementDecision(new Rect(936, 527, 36, 137), 1600, 900);

        Assert.Equal(decision1080P, decision900P);
        Assert.False(decision1080P.Pickup);
        Assert.False(decision1080P.Left);
        Assert.True(decision1080P.Right);
        Assert.False(decision1080P.Forward);
        Assert.False(decision1080P.Backward);
    }

    [Fact]
    public void GetMovementDecision_ShouldPickCenteredGroundDropInsteadOfWalkingPastIt()
    {
        var decision = ScanPickTask.GetMovementDecision(new Rect(934, 685, 38, 238), 1920, 1080);

        Assert.True(decision.Pickup);
        Assert.False(decision.Left);
        Assert.False(decision.Right);
        Assert.False(decision.Forward);
        Assert.False(decision.Backward);
    }

    [Fact]
    public void GetMovementDecision_ShouldSteerTowardsDistantDiagonalDrop()
    {
        var decision = ScanPickTask.GetMovementDecision(new Rect(1400, 300, 40, 100), 1920, 1080);

        Assert.False(decision.Pickup);
        Assert.False(decision.Left);
        Assert.True(decision.Right);
        Assert.True(decision.Forward);
        Assert.False(decision.Backward);
    }

    [Fact]
    public void LootScanCompletionPolicy_ShouldStopAfterStableEmptyCoverage()
    {
        var policy = new LootScanCompletionPolicy(requiredEmptySweeps: 2, minimumScanDuration: TimeSpan.FromSeconds(5));

        Assert.False(policy.ObserveSweep(foundItems: false, elapsed: TimeSpan.FromSeconds(3)));
        Assert.True(policy.ObserveSweep(foundItems: false, elapsed: TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void LootScanCompletionPolicy_ShouldResetAfterFindingLoot()
    {
        var policy = new LootScanCompletionPolicy(requiredEmptySweeps: 2, minimumScanDuration: TimeSpan.FromSeconds(5));

        Assert.False(policy.ObserveSweep(foundItems: false, elapsed: TimeSpan.FromSeconds(3)));
        Assert.False(policy.ObserveSweep(foundItems: true, elapsed: TimeSpan.FromSeconds(5)));
        Assert.False(policy.ObserveSweep(foundItems: false, elapsed: TimeSpan.FromSeconds(7)));
        Assert.True(policy.ObserveSweep(foundItems: false, elapsed: TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void CreateCompletionPolicy_ShouldFinishAfterOneCompleteEmptySweep()
    {
        var policy = ScanPickTask.CreateCompletionPolicy(configuredSeconds: 15);

        Assert.False(policy.ObserveSweep(foundItems: true, elapsed: TimeSpan.FromSeconds(1)));
        Assert.True(policy.ObserveSweep(foundItems: false, elapsed: TimeSpan.FromSeconds(4)));
    }

    [Fact]
    public void LootTargetStallPolicy_ShouldDetectRepeatedUnchangedPickupCandidate()
    {
        var policy = new LootTargetStallPolicy(
            requiredRepeatedPickupAttempts: 3,
            coordinateTolerance: 10);
        var target = new Rect(754, 776, 86, 91);

        Assert.False(policy.Observe(target, pickupAttempted: true));
        Assert.False(policy.Observe(new Rect(758, 778, 85, 90), pickupAttempted: true));
        Assert.True(policy.Observe(new Rect(755, 777, 86, 91), pickupAttempted: true));
    }

    [Fact]
    public void LootTargetStallPolicy_ShouldResetWhenApproachingDifferentCandidate()
    {
        var policy = new LootTargetStallPolicy(
            requiredRepeatedPickupAttempts: 3,
            coordinateTolerance: 10);
        var target = new Rect(754, 776, 86, 91);

        Assert.False(policy.Observe(target, pickupAttempted: true));
        Assert.False(policy.Observe(target, pickupAttempted: false));
        Assert.False(policy.Observe(target, pickupAttempted: true));
    }
}
