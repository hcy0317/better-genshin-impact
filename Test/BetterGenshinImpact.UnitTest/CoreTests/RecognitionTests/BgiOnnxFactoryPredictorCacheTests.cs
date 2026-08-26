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
}
