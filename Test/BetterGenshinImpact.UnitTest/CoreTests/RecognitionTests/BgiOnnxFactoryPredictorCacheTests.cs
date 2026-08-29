using BetterGenshinImpact.Core.Recognition.ONNX;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;

namespace BetterGenshinImpact.UnitTest.CoreTests.RecognitionTests;

public class BgiOnnxFactoryPredictorCacheTests
{
    [Fact]
    public void GetOrCreateYoloPredictor_ShouldReuseOnlyTheSameModel()
    {
        using var factory = new BgiOnnxFactory(new FakeLogger<BgiOnnxFactory>());

        var firstTree = factory.GetOrCreateYoloPredictor(BgiOnnxModel.BgiTree);
        var secondTree = factory.GetOrCreateYoloPredictor(BgiOnnxModel.BgiTree);
        var world = factory.GetOrCreateYoloPredictor(BgiOnnxModel.BgiWorld);

        Assert.Same(firstTree, secondTree);
        Assert.NotSame(firstTree, world);
    }

    [Fact]
    public void FailedSharedPredictor_ShouldBeEvictedSoTheNextCallCanRetry()
    {
        using var factory = new BgiOnnxFactory(new FakeLogger<BgiOnnxFactory>());
        var failed = factory.GetOrCreateYoloPredictor(BgiOnnxModel.BgiTree);

        factory.EvictFailedSharedPredictor(BgiOnnxModel.BgiTree, failed);
        var retried = factory.GetOrCreateYoloPredictor(BgiOnnxModel.BgiTree);

        Assert.NotSame(failed, retried);
    }

    [Fact]
    public void InitializationProgress_ShouldReportEveryFifteenSeconds()
    {
        var progress = new OnnxInitializationProgress();

        Assert.False(progress.Observe(TimeSpan.FromSeconds(14)).ShouldLog);
        Assert.True(progress.Observe(TimeSpan.FromSeconds(15)).ShouldLog);
        Assert.False(progress.Observe(TimeSpan.FromSeconds(29)).ShouldLog);
        Assert.True(progress.Observe(TimeSpan.FromSeconds(30)).ShouldLog);
    }

    [Fact]
    public async Task InitializationTask_ShouldShareWorkAndReportProgressForSyncAndAsyncCallers()
    {
        var logger = new CollectingLogger();
        var invocationCount = 0;
        var initialization = new OnnxInitializationTask<int>(
            "TestModel",
            () =>
            {
                Interlocked.Increment(ref invocationCount);
                Thread.Sleep(80);
                return 42;
            },
            logger,
            TimeSpan.FromMilliseconds(10));

        var asyncValue = initialization.GetValueAsync(CancellationToken.None);
        var syncValue = Task.Run(() => initialization.Value);

        Assert.Equal(42, await asyncValue);
        Assert.Equal(42, await syncValue);
        Assert.Equal(1, invocationCount);
        Assert.Contains(logger.Messages, message => message.Contains("开始初始化模型 TestModel"));
        Assert.Contains(logger.Messages, message => message.Contains("仍在初始化"));
        Assert.Contains(logger.Messages, message => message.Contains("初始化完成"));
    }

    [Fact]
    public async Task DisposeDuringInitializationDisposesTheLatePredictor()
    {
        using var factoryEntered = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var resource = new TrackingDisposable();
        using var initialization = new OnnxInitializationTask<TrackingDisposable>(
            "LateModel",
            () =>
            {
                factoryEntered.Set();
                releaseFactory.Wait();
                return resource;
            },
            new CollectingLogger(),
            TimeSpan.FromMilliseconds(10),
            value => value.Dispose());
        var valueTask = initialization.GetValueAsync(CancellationToken.None);
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(2)));

        initialization.Dispose();
        releaseFactory.Set();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await valueTask);
        Assert.True(SpinWait.SpinUntil(() => resource.IsDisposed, TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task CancelledWaiterDoesNotPoisonTheSharedInitialization()
    {
        using var factoryEntered = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var initialization = new OnnxInitializationTask<int>(
            "SharedModel",
            () =>
            {
                factoryEntered.Set();
                releaseFactory.Wait();
                return 42;
            },
            new CollectingLogger(),
            TimeSpan.FromMilliseconds(10));
        var cancelledWait = initialization.GetValueAsync(cancellation.Token);
        Assert.True(factoryEntered.Wait(TimeSpan.FromSeconds(2)));

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await cancelledWait);
        releaseFactory.Set();

        Assert.Equal(42, await initialization.GetValueAsync(CancellationToken.None));
    }

    [Fact]
    public void DisposeBeforeInitializationReleasesCapturedFactoryResources()
    {
        var factoryCalls = 0;
        var released = false;
        var initialization = new OnnxInitializationTask<int>(
            "NeverStarted",
            () =>
            {
                factoryCalls++;
                return 42;
            },
            new CollectingLogger(),
            disposeUnstarted: () => released = true);

        initialization.Dispose();

        Assert.True(released);
        Assert.Equal(0, factoryCalls);
        Assert.Throws<ObjectDisposedException>(() => _ = initialization.Value);
    }

    private sealed class CollectingLogger : ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Enqueue(formatter(state, exception));
        }
    }

    private sealed class TrackingDisposable : IDisposable
    {
        private int _disposed;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
        }
    }
}
