using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.ArtifactAnalysis;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactHostTaskScopeTests
{
    [Fact]
    public async Task ChildrenShareTheRealTaskScopeWithoutNestedAdmissionOrAppStartup()
    {
        using var task = TaskExecutionScope.BeginOwned();
        using var scope = ArtifactHostTaskScope.Enter(CancellationToken.None);
        var first = new RecordingTask();
        var second = new RecordingTask();
        var owner = TaskExecutionScope.DiagnosticId;

        await ArtifactHostTaskScope.RunSoloTaskAsync(first, CancellationToken.None);
        await Task.Yield();
        await ArtifactHostTaskScope.RunSoloTaskAsync(second, CancellationToken.None, requireContinuousOwnership: true);

        Assert.Equal(1, first.Starts);
        Assert.Equal(1, second.Starts);
        Assert.Equal(owner, first.Owner);
        Assert.Equal(owner, second.Owner);
    }

    [Fact]
    public async Task ACancelledParentDoesNotStartAChild()
    {
        using var cancelled = new CancellationTokenSource();
        using var task = TaskExecutionScope.BeginOwned();
        using var scope = ArtifactHostTaskScope.Enter(cancelled.Token);
        var child = new RecordingTask();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ArtifactHostTaskScope.RunSoloTaskAsync(child, CancellationToken.None));
        Assert.Equal(0, child.Starts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InheritedContextCannotStartAfterHostOrRealTaskCloses(bool closeHostScope)
    {
        using var task = TaskExecutionScope.BeginOwned();
        using var scope = ArtifactHostTaskScope.Enter(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var child = new RecordingTask();
        var late = Task.Run(async () =>
        {
            await resume.Task.WaitAsync(timeout.Token);
            await ArtifactHostTaskScope.RunSoloTaskAsync(child, CancellationToken.None);
        });

        if (closeHostScope) scope.Dispose();
        else task.Dispose();
        resume.SetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => late);
        Assert.Equal(0, child.Starts);
    }

    [Fact]
    public async Task LatchedTaskFailurePreventsStartingAnotherChild()
    {
        using var task = TaskExecutionScope.BeginOwned();
        using var scope = ArtifactHostTaskScope.Enter(CancellationToken.None);
        var child = new RecordingTask();
        var failure = new TaskFailureRecoveryException(
            new InvalidOperationException("preflight failed"), new InvalidOperationException("recovery failed"));
        TaskExecutionScope.Capture().Report(failure);

        var error = await Assert.ThrowsAsync<TaskFailureRecoveryException>(() =>
            ArtifactHostTaskScope.RunSoloTaskAsync(child, CancellationToken.None));
        Assert.Same(failure, error);
        Assert.Equal(0, child.Starts);
    }

    [Fact]
    public async Task PreparedLockObservationCannotBeUsedOutsideItsContinuousOwnership()
    {
        var child = new RecordingTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ArtifactHostTaskScope.RunSoloTaskAsync(child, CancellationToken.None, requireContinuousOwnership: true));
        Assert.Equal(0, child.Starts);
    }

    private sealed class RecordingTask : ISoloTask
    {
        public string Name => "isolated";
        public int Starts { get; private set; }
        public Guid? Owner { get; private set; }
        public Task Start(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Starts++;
            Owner = TaskExecutionScope.DiagnosticId;
            return Task.CompletedTask;
        }
    }
}
