using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class ClaimEncounterPointsRewardsTaskTests
{
    [Fact]
    public void CaptureScopeDisposesCaptureBeforeReturningToNextStep()
    {
        var events = new List<string>();
        var capture = new RecordingDisposable(() => events.Add("dispose"));

        var claimed = CaptureScope.Use(capture, currentCapture =>
        {
            Assert.False(currentCapture.IsDisposed);
            events.Add("recognize");
            return true;
        });
        events.Add("next-capture");

        Assert.True(claimed);
        Assert.True(capture.IsDisposed);
        Assert.Equal(["recognize", "dispose", "next-capture"], events);
    }

    private sealed class RecordingDisposable(Action onDispose) : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
            onDispose();
        }
    }
}
