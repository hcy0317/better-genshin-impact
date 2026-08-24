using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class TpTaskMapDragTests
{
    [Theory]
    [InlineData(100, -200, 100, -200)]
    [InlineData(600, 800, 180, 240)]
    [InlineData(-600, -800, -180, -240)]
    public void LimitMapDragDelta_ShouldPreserveDirectionAndCapDistance(
        int deltaX,
        int deltaY,
        int expectedX,
        int expectedY)
    {
        var actual = TpTask.LimitMapDragDelta(deltaX, deltaY);

        Assert.Equal((expectedX, expectedY), actual);
    }

    [Fact]
    public void IsMapMoveRecognitionAnomaly_ShouldAllowCappedReverseFeedbackToRecoverByIteration()
    {
        var actual = TpTask.IsMapMoveRecognitionAnomaly(
            expectedMoveLen: 300,
            actualMoveLen: 300,
            moveRatio: 1,
            moveDirectionCos: -1,
            jumpDistance: 600);

        Assert.False(actual);
    }

    [Fact]
    public void IsMapMoveRecognitionAnomaly_ShouldAllowCappedNoProgressFeedbackToRecoverByIteration()
    {
        var actual = TpTask.IsMapMoveRecognitionAnomaly(
            expectedMoveLen: 300,
            actualMoveLen: 0,
            moveRatio: 0,
            moveDirectionCos: 1,
            jumpDistance: 300);

        Assert.False(actual);
    }

    [Fact]
    public void IsMapMoveRecognitionAnomaly_ShouldStillRejectImpossibleCoordinateJump()
    {
        var actual = TpTask.IsMapMoveRecognitionAnomaly(
            expectedMoveLen: 300,
            actualMoveLen: 300,
            moveRatio: 1,
            moveDirectionCos: 1,
            jumpDistance: 601);

        Assert.True(actual);
    }

    [Fact]
    public void IsMapMoveRecognitionAnomaly_ShouldAcceptConsistentSmallMovement()
    {
        var actual = TpTask.IsMapMoveRecognitionAnomaly(
            expectedMoveLen: 120,
            actualMoveLen: 108,
            moveRatio: 0.9,
            moveDirectionCos: 0.98,
            jumpDistance: 15);

        Assert.False(actual);
    }

    [Theory]
    [InlineData("Teyvat", "枫丹", "Teyvat", "蒙德", true)]
    [InlineData("Teyvat", "蒙德", "Teyvat", "蒙德", false)]
    [InlineData("Enkanomiya", null, "Teyvat", "蒙德", true)]
    [InlineData(null, null, "Teyvat", "蒙德", false)]
    [InlineData("Teyvat", "枫丹", "Teyvat", null, false)]
    public void ShouldForceSwitchToTargetCountry_UsesLastSuccessfulTeleport(
        string? lastMapName,
        string? lastCountry,
        string targetMapName,
        string? targetCountry,
        bool expected)
    {
        var actual = TpTask.ShouldForceSwitchToTargetCountry(
            lastMapName,
            lastCountry,
            targetMapName,
            targetCountry);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void ShouldCloseBigMapAfterTeleportFailure_OnlyClosesAnOpenMap(
        bool isInBigMapUi,
        bool expected)
    {
        Assert.Equal(expected, TpTask.ShouldCloseBigMapAfterTeleportFailure(isInBigMapUi));
    }
}
