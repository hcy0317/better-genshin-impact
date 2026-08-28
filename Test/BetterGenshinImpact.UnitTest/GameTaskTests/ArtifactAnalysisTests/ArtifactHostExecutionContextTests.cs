using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactHostExecutionContextTests
{
    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(false, 2, true)]
    [InlineData(true, 1, true)]
    public void ForwardRetryBecomesRecoveryAfterTheFirstClaim(
        bool launchRecovery,
        int attempt,
        bool expected)
    {
        Assert.Equal(
            expected,
            ArtifactHostService.IsRecoveryAttempt(launchRecovery, attempt));
    }

    [Fact]
    public async Task RunAsync_PostsWatcherWorkToCapturedUiContext()
    {
        var context = new RecordingSynchronizationContext();
        SynchronizationContext? observedContext = null;

        await Task.Run(() => ArtifactHostExecutionContext.RunAsync(context, () =>
        {
            observedContext = SynchronizationContext.Current;
            return Task.CompletedTask;
        }));

        Assert.Equal(1, context.PostCalls);
        Assert.Same(context, observedContext);
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public int PostCalls { get; private set; }

        public override void Post(SendOrPostCallback callback, object? state)
        {
            PostCalls++;
            var previous = Current;
            SetSynchronizationContext(this);
            try
            {
                callback(state);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }
    }
}
