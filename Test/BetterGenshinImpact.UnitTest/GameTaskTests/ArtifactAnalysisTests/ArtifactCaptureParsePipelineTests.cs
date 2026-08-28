using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using System.Runtime.CompilerServices;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.ArtifactAnalysisTests;

public class ArtifactCaptureParsePipelineTests
{
    [Fact]
    public async Task RunAsync_CapturesNextFrameWhilePreviousFrameIsParsing()
    {
        var parsingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParsing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFrameCaptured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<TestFrame>();

        async IAsyncEnumerable<TestFrame> CaptureFrames(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var first = new TestFrame(1); frames.Add(first); yield return first;
            await parsingStarted.Task.WaitAsync(cancellationToken);
            var second = new TestFrame(2); frames.Add(second); secondFrameCaptured.TrySetResult(); yield return second;
        }

        var pipeline = ArtifactCaptureParsePipeline.RunAsync(
            CaptureFrames(),
            async (frame, cancellationToken) =>
            {
                if (frame.Id == 1)
                {
                    parsingStarted.TrySetResult();
                    await releaseParsing.Task.WaitAsync(cancellationToken);
                }
                return frame.Id;
            },
            capacity: 2,
            CancellationToken.None);

        await secondFrameCaptured.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(pipeline.IsCompleted);
        releaseParsing.TrySetResult();

        Assert.Equal([1, 2], await pipeline);
        Assert.All(frames, frame => Assert.True(frame.Disposed));
    }

    private sealed class TestFrame(int id) : IDisposable
    {
        public int Id { get; } = id;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
