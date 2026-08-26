using BetterGenshinImpact.Service;
using Fischless.WindowsInput;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class GameStartupInputRecoveryTests
{
    [Fact]
    public void TryExecute_ShouldKeepStartupWaitingWhenRdpInputDispatchTemporarilyFails()
    {
        var inputFailure = new InputDispatchException("SendInput temporarily unavailable");

        var succeeded = GameStartupInputRecovery.TryExecute(
            () => throw inputFailure,
            out var observedFailure);

        Assert.False(succeeded);
        Assert.Same(inputFailure, observedFailure);
    }

    [Fact]
    public void TryExecute_ShouldNotHideUnrelatedStartupFailures()
    {
        var unrelatedFailure = new InvalidOperationException("unrelated startup failure");

        var observedFailure = Assert.Throws<InvalidOperationException>(() =>
            GameStartupInputRecovery.TryExecute(
                () => throw unrelatedFailure,
                out _));

        Assert.Same(unrelatedFailure, observedFailure);
    }

    [Fact]
    public void StartupWaitHeartbeat_ShouldReportEveryThirtySecondsWithoutLogSpam()
    {
        var heartbeat = new GameStartupWaitHeartbeat(TimeSpan.FromSeconds(30));

        Assert.False(heartbeat.ShouldReport(TimeSpan.FromSeconds(29)));
        Assert.True(heartbeat.ShouldReport(TimeSpan.FromSeconds(30)));
        Assert.False(heartbeat.ShouldReport(TimeSpan.FromSeconds(59)));
        Assert.True(heartbeat.ShouldReport(TimeSpan.FromSeconds(60)));
        Assert.True(heartbeat.ShouldReport(TimeSpan.FromSeconds(95)));
        Assert.False(heartbeat.ShouldReport(TimeSpan.FromSeconds(119)));
        Assert.True(heartbeat.ShouldReport(TimeSpan.FromSeconds(120)));
    }
}
