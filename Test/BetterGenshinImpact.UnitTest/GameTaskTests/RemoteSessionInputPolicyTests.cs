using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class RemoteSessionInputPolicyTests
{
    [Theory]
    [InlineData(true, "Idle", true)]
    [InlineData(true, "idle", true)]
    [InlineData(true, "YuanShen", false)]
    [InlineData(true, "explorer", false)]
    [InlineData(false, "Idle", false)]
    [InlineData(true, null, false)]
    public void ShouldActivateWithoutForegroundVerification_ShouldOnlyAllowIdleInRemoteSession(
        bool isTerminalServerSession,
        string? activeProcessName,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteSessionInputPolicy.ShouldActivateWithoutForegroundVerification(
                isTerminalServerSession,
                activeProcessName));
    }

    [Theory]
    [InlineData(false, true, false, 1000, 0, 500, false)]
    [InlineData(true, false, false, 1000, 0, 500, false)]
    [InlineData(true, true, true, 1000, 0, 500, false)]
    [InlineData(true, true, false, 499, 0, 500, false)]
    [InlineData(true, true, false, 500, 0, 500, true)]
    [InlineData(true, true, false, 1000, 500, 500, true)]
    public void ShouldActivateBeforeInput_ShouldRequireInactiveInitializedRemoteSessionAfterInterval(
        bool isTerminalServerSession,
        bool isTaskContextInitialized,
        bool isGameWindowActive,
        long now,
        long lastActivation,
        long activationIntervalMilliseconds,
        bool expected)
    {
        Assert.Equal(
            expected,
            RemoteSessionInputPolicy.ShouldActivateBeforeInput(
                isTerminalServerSession,
                isTaskContextInitialized,
                isGameWindowActive,
                now,
                lastActivation,
                activationIntervalMilliseconds));
    }
}
