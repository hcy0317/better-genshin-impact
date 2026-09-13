using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class TaskAdmissionGateTests
{
    [Fact]
    public async Task ClosingWaitsForAsyncStartupBeforeItsFirstTaskSemaphoreAcquisition()
    {
        var gate = new TaskAdmissionGate();
        var startup = gate.EnterActivity();
        gate.SetClosing(true);
        var drain = gate.WaitForActivitiesAsync();
        Assert.False(drain.IsCompleted);
        Assert.Throws<OperationCanceledException>(() => gate.EnterActivity());
        startup.Dispose();
        await drain;
    }

    [Fact]
    public void DrainingRejectsNewCancellationContextsUntilEveryPauseIsReleased()
    {
        var gate = new TaskAdmissionGate();
        var initializations = 0;
        using (gate.Pause())
        {
            using (gate.Pause()) Assert.Throws<OperationCanceledException>(() => gate.Initialize(() => initializations++));
            Assert.Throws<OperationCanceledException>(gate.Check);
        }
        gate.Initialize(() => initializations++);
        Assert.Equal(1, initializations);
        gate.SetClosing(true);
        Assert.Throws<OperationCanceledException>(() => gate.Initialize(() => initializations++));
        gate.SetClosing(false);
        gate.Check();
    }

    [Fact]
    public async Task RejectedStartupReleasesItsAlreadyAcquiredSemaphore()
    {
        var gate = new TaskAdmissionGate();
        using var semaphore = new SemaphoreSlim(1);
        using var pause = gate.Pause();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            BetterGenshinImpact.GameTask.TaskRunnerStartupGate.TryAcquireAsync(semaphore, () => gate.Initialize(() => { })));
        Assert.Equal(1, semaphore.CurrentCount);
    }
}
