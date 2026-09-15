using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.Model;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class AutomationRunCoordinatorTests
{
    [Fact]
    public async Task AnotherRunCannotReadConfigurationOrReplaceCancellationDuringStartup()
    {
        var cancellation = new CancellationContext();
        var admission = new TaskAdmissionGate();
        using var input = new SemaphoreSlim(1);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken firstToken = default;
        var first = AutomationRunCoordinator.RunAsync(cancellation, admission, input, async () =>
        { firstToken = cancellation.GetTokenOrNone(); started.SetResult(); await release.Task; });
        try
        {
            await started.Task;
            var readConfiguration = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => AutomationRunCoordinator.RunAsync(cancellation,
                admission, input, () => { readConfiguration = true; return Task.CompletedTask; }));
            Assert.False(readConfiguration);
            Assert.Equal(firstToken, cancellation.GetTokenOrNone());
            Assert.False(firstToken.IsCancellationRequested);
            Assert.False(admission.WaitForActivitiesAsync().IsCompleted);
        }
        finally { release.TrySetResult(); await first; }
        await admission.WaitForActivitiesAsync();
        Assert.Equal(1, input.CurrentCount);
    }

    [Fact]
    public async Task LateWorkFromAnOldRunCannotResetCancelOrClearTheNewGeneration()
    {
        var cancellation = new CancellationContext();
        var admission = new TaskAdmissionGate();
        using var input = new SemaphoreSlim(1);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? late = null;
        await AutomationRunCoordinator.RunAsync(cancellation, admission, input, () =>
        {
            late = Task.Run(async () =>
            {
                await resume.Task;
                Assert.Throws<OperationCanceledException>(cancellation.Set);
                Assert.Throws<OperationCanceledException>(cancellation.CheckLifecycleAccess);
                cancellation.Clear();
                cancellation.Cancel();
                Assert.True(cancellation.GetTokenOrNone().IsCancellationRequested);
            });
            return Task.CompletedTask;
        });
        await AutomationRunCoordinator.RunAsync(cancellation, admission, input, async () =>
        {
            var token = cancellation.GetTokenOrNone();
            resume.SetResult();
            await late!;
            Assert.Equal(token, cancellation.GetTokenOrNone());
            Assert.False(token.IsCancellationRequested);
        });
    }

    [Fact]
    public async Task FinalReceiptStillOwnsTheRunAndOriginalFailureSurvivesCancellationCleanup()
    {
        var cancellation = new CancellationContext();
        var admission = new TaskAdmissionGate();
        using var input = new SemaphoreSlim(1);
        var original = new IOException("original task failure");
        Exception? reported = null;
        await AutomationRunCoordinator.RunAsync(cancellation, admission, input, () =>
        {
            cancellation.GetTokenOrNone().Register(() => throw new InvalidOperationException("cleanup failure"));
            throw original;
        }, (error, cancelled) =>
        {
            reported = error;
            Assert.False(cancelled);
            Assert.False(admission.WaitForActivitiesAsync().IsCompleted);
            Assert.ThrowsAny<Exception>(() => cancellation.EnterRun());
            return Task.CompletedTask;
        });
        Assert.Same(original, reported);
        Assert.True(original.Data.Contains("RunCancellationCleanup"));
        await admission.WaitForActivitiesAsync();
        Assert.Equal(1, input.CurrentCount);
    }

    [Fact]
    public void RunSnapshotDoesNotFollowLaterTaskOrCompletionConfigurationEdits()
    {
        var config = new OneDragonFlowConfig { Name = "first", CompletionAction = "关闭软件" };
        var item = new OneDragonTaskItem("领取邮件", "mail") { IsEnabled = true };
        config.TaskOrder.Add(item.Id);
        config.TaskEnabledList[item.Id] = true;
        var snapshot = OneDragonRunSnapshot.Capture(config, [item]);
        config.Name = "second";
        config.CompletionAction = "关机";
        config.TaskEnabledList[item.Id] = false;
        config.TaskOrder.Clear();
        item.IsEnabled = false;
        item.Name = "其他任务";
        Assert.Equal("first", snapshot.Config.Name);
        Assert.Equal("关闭软件", snapshot.Config.CompletionAction);
        Assert.True(snapshot.Config.TaskEnabledList["mail"]);
        Assert.Single(snapshot.Config.TaskOrder);
        Assert.True(snapshot.Tasks[0].IsEnabled);
        Assert.Equal("领取邮件", snapshot.Tasks[0].Name);
    }
}
