using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.ViewModel.Pages;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class OneDragonRecoveryFinalizationTests
{
    [Fact]
    public async Task FailedRecoveryStopsLaterGameStepsButSummarizesAndCompletes()
    {
        var taskFailure = new IOException("task");
        var recoveryFailure = new TimeoutException("recovery");
        var failure = new TaskFailureRecoveryException(taskFailure, recoveryFailure);
        var events = new List<string>();
        var outcomes = new ScriptOutcomeAccumulator();
        var actual = await Assert.ThrowsAsync<TaskFailureRecoveryException>(async () =>
        {
            try
            {
                await OneDragonStepRunner.RunAsync(true, (action, _) => action(),
                    () => Task.FromException(taskFailure), _ => Task.FromException(failure));
                events.Add("later game task");
            }
            catch (TaskFailureRecoveryException error)
            {
                await OneDragonFinalizer.RunAfterRecoveryFailureAsync(error, () =>
                {
                    outcomes.Add("first task", new(ScriptOutcomeKind.Failed, error.Message));
                    Assert.Equal(ScriptOutcomeKind.Failed, outcomes.Complete().Kind);
                    events.Add("summary");
                }, () => events.Add("completion"));
            }
        });
        Assert.Same(failure, actual);
        Assert.Equal(new[] { "summary", "completion" }, events);
    }

    [Fact]
    public async Task SummaryFailureStillCompletesAndPreservesAllFailures()
    {
        var failure = new IOException("original recovery failure");
        var summary = new IOException("summary failure");
        var completion = new IOException("completion failure");
        var result = await Assert.ThrowsAsync<AggregateException>(() => OneDragonFinalizer.RunAfterRecoveryFailureAsync(
            failure, () => throw summary, () => throw completion));
        Assert.Equal(new Exception[] { completion, failure, summary }.OrderBy(e => e.Message),
            result.Flatten().InnerExceptions.OrderBy(e => e.Message));
    }

    [Fact]
    public async Task CancellationDoesNotStartFinalization()
    {
        var failure = new OperationCanceledException();
        var called = false;
        var actual = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            OneDragonFinalizer.RunAfterRecoveryFailureAsync(failure, () => called = true, () => called = true));
        Assert.Same(failure, actual);
        Assert.False(called);
    }
}
