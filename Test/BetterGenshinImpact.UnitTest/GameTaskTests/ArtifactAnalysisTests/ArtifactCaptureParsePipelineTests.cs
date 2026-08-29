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

    [Fact]
    public async Task ConsumerFailureDisposesQueuedAndPendingFramesExactlyOnce()
    {
        var parsingStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thirdFrameCreated = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var frames = new List<TestFrame>();

        async IAsyncEnumerable<TestFrame> CaptureFrames(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            for (var id = 1; id <= 3; id++)
            {
                var frame = new TestFrame(id);
                frames.Add(frame);
                if (id == 3) thirdFrameCreated.TrySetResult();
                yield return frame;
                await Task.Yield();
            }
        }

        var pipeline = ArtifactCaptureParsePipeline.RunAsync(
            CaptureFrames(),
            async (frame, cancellationToken) =>
            {
                if (frame.Id == 1)
                {
                    parsingStarted.TrySetResult();
                    await releaseFailure.Task.WaitAsync(cancellationToken);
                    throw new InvalidOperationException("parse failed");
                }
                return frame.Id;
            },
            capacity: 1,
            CancellationToken.None);

        await parsingStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await thirdFrameCreated.Task.WaitAsync(TimeSpan.FromSeconds(1));
        releaseFailure.TrySetResult();

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline);
        Assert.All(frames, frame => Assert.Equal(1, frame.DisposeCount));
    }

    private sealed class TestFrame(int id) : IDisposable
    {
        public int Id { get; } = id;
        public int DisposeCount { get; private set; }
        public bool Disposed => DisposeCount > 0;
        public void Dispose() => DisposeCount++;
    }
}
