using BetterGenshinImpact.GameTask.Common;
using Fischless.GameCapture;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class TaskControlCaptureTests
{
    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(false, true, false, false)]
    public void DispatcherCapturePolicy_YieldsForegroundCaptureToIndependentTasks(
        bool gameActive,
        bool independentTaskRunning,
        bool hasEnabledTriggers,
        bool expected)
    {
        Assert.Equal(expected, TaskTriggerCapturePolicy.ShouldSkip(
            gameActive,
            independentTaskRunning,
            hasEnabledTriggers));
    }

    [Fact]
    public void CaptureGameImageRetriesWithoutEnteringTaskSleep()
    {
        using var capture = new EmptyGameCapture();
        var retryDelays = new List<int>();

        var exception = Assert.Throws<Exception>(() =>
            GameCaptureRetry.Capture(capture, retryDelays.Add));

        Assert.Equal("尝试多次后,截图失败!", exception.Message);
        Assert.Equal(4, capture.CaptureCount);
        Assert.Equal([30, 30, 30], retryDelays);
    }

    [Fact]
    public void GameExitGuardStopsFocusRecoveryImmediately()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            GameProcessExitGuard.ThrowIfExited(true, () => true));

        Assert.Equal("检测到原神进程已退出，停止窗口焦点恢复。", exception.Message);
    }

    [Fact]
    public void GameExitGuardDoesNotReadProcessBeforeContextInitialization()
    {
        var processWasRead = false;

        GameProcessExitGuard.ThrowIfExited(false, () =>
        {
            processWasRead = true;
            return true;
        });

        Assert.False(processWasRead);
    }

    private sealed class EmptyGameCapture : IGameCapture
    {
        public int CaptureCount { get; private set; }

        public bool IsCapturing => true;

        public void Start(nint hWnd, Dictionary<string, object>? settings = null)
        {
        }

        public GameCaptureFrame? Capture()
        {
            CaptureCount++;
            return null;
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }
}
