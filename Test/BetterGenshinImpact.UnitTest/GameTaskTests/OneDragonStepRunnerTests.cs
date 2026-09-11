using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class OneDragonStepRunnerTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task BothEntryModesRecoverUnderOwnershipAndStopWhenRecoveryFails(bool reportFailure, bool recoverable)
    {
        var taskError = new InvalidOperationException("task failed");
        var recoveryError = new TimeoutException("still in domain");
        var owned = false;
        var recordedFailure = false;
        var continued = false;
        async Task RunOwned(Func<Task> action, bool propagate)
        {
            owned = true;
            try { await action(); }
            catch { recordedFailure = true; if (propagate) throw; }
            finally { owned = false; }
        }
        async Task<Exception?> Run()
        {
            var result = await OneDragonStepRunner.RunAsync(reportFailure, RunOwned,
                () => throw taskError,
                error =>
                {
                    Assert.True(owned);
                    Assert.Same(taskError, error);
                    return TaskFailureRecoveryPolicy.RecoverOrThrowAsync(error,
                        () => recoverable ? Task.CompletedTask : throw recoveryError);
                });
            continued = true;
            return result;
        }
        if (recoverable)
        {
            Assert.Same(reportFailure ? taskError : null, await Run());
            Assert.True(continued);
        }
        else
        {
            var error = await Assert.ThrowsAsync<TaskFailureRecoveryException>(Run);
            Assert.Equal(new Exception[] { taskError, recoveryError }, error.InnerExceptions);
            Assert.False(continued);
        }
        Assert.True(recordedFailure);
        Assert.False(owned);
    }

    [Fact]
    public async Task CancellationNeverRecoversOrAdvancesEvenInGuiMode()
    {
        var recovered = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => OneDragonStepRunner.RunAsync(false,
            (action, _) => action(), () => throw new OperationCanceledException(),
            _ => { recovered = true; return Task.CompletedTask; }));
        Assert.False(recovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WrappedTerminalRecoveryFailureNeverStartsAnotherRecovery(bool reportFailure)
    {
        var failure = new InvalidOperationException("wrapper", new TaskFailureRecoveryException(
            new InvalidOperationException("task"), new TimeoutException("recovery")));
        var recovered = false;
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => OneDragonStepRunner.RunAsync(reportFailure,
            (action, _) => action(), () => throw failure,
            _ => { recovered = true; return Task.CompletedTask; }));
        Assert.Same(failure, actual);
        Assert.False(recovered);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulRecoveryDoesNotSuppressAdditionalCleanupFailure(bool reportFailure)
    {
        var taskFailure = new InvalidOperationException("task");
        var cleanupFailure = new InvalidOperationException("input release");
        async Task RunOwned(Func<Task> action, bool propagate)
        {
            Assert.True(propagate);
            try { await action(); }
            catch (Exception error) { throw new AggregateException(error, cleanupFailure); }
        }
        var actual = await Assert.ThrowsAsync<AggregateException>(() => OneDragonStepRunner.RunAsync(reportFailure,
            RunOwned, () => throw taskFailure, _ => Task.CompletedTask));
        Assert.Equal(new Exception[] { taskFailure, cleanupFailure }, actual.InnerExceptions);
    }
}
