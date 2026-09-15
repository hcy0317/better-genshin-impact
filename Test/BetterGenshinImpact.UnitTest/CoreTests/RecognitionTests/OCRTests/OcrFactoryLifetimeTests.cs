using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.ONNX;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BetterGenshinImpact.UnitTest.CoreTests.RecognitionTests.OCRTests;

public class OcrFactoryLifetimeTests
{
    [Fact]
    public async Task RepeatedModelChangesKeepLifecycleDiagnosticsBounded()
    {
        var messages = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var factory = new OcrFactory(new RecordingLogger(messages), () => new NativeOcr());
        for (var i = 0; i < 40; i++) { await factory.PrepareAsync(); await factory.Unload(); }
        await factory.DisposeAsync();
        Assert.InRange(messages.Count(message => message.StartsWith("OCR_LIFECYCLE")), 1, 65);
    }

    [Fact]
    public async Task SynchronousDisposalInsideTheNativeBorrowIsRejectedRatherThanWaitingForItself()
    {
        OcrFactory? factory = null;
        var native = new NativeOcr(() => Assert.Throws<InvalidOperationException>(() => factory!.Dispose()));
        using (factory = new OcrFactory(NullLogger<BgiOnnxFactory>.Instance, () => native))
        {
            using var mat = new Mat(1, 1, MatType.CV_8UC3);
            Assert.Equal("recognized", factory.PaddleOcr.Ocr(mat));
            await factory.Unload();
            Assert.True(native.Disposed);
        }
    }

    [Fact]
    public async Task ConcurrentPreparationSharesOneNativeInstanceAndCancellationOnlyCancelsTheWaiter()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var created = 0;
        var native = new NativeOcr();
        using var factory = new OcrFactory(NullLogger<BgiOnnxFactory>.Instance, () =>
        {
            Interlocked.Increment(ref created);
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return native;
        });
        var cancelledWait = factory.PrepareAsync(cancellation.Token);
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var otherWaits = Enumerable.Range(0, 8).Select(_ => factory.PrepareAsync()).ToArray();
        cancellation.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWait);
            Assert.Equal(1, created);
            Assert.False(native.Disposed);
        }
        finally { release.Set(); }
        await Task.WhenAll(otherWaits).WaitAsync(TimeSpan.FromSeconds(5));
        await factory.Unload();
        Assert.Equal(1, native.DisposeCount);
    }

    [Fact]
    public async Task UnloadDuringConstructionRetiresTheLateInstanceAndRejectsNewBorrowers()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = new NativeOcr();
        var second = new NativeOcr();
        var created = 0;
        using var factory = new OcrFactory(NullLogger<BgiOnnxFactory>.Instance, () =>
        {
            if (Interlocked.Increment(ref created) != 1) return second;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
            return first;
        });
        var preparing = factory.PrepareAsync();
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var unload = factory.Unload();
        try
        {
            Assert.False(unload.IsCompleted);
            await Assert.ThrowsAsync<InvalidOperationException>(() => factory.PrepareAsync());
        }
        finally { release.Set(); }
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparing);
        await unload.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, first.DisposeCount);
        await factory.PrepareAsync();
        Assert.Equal(2, created);
        Assert.False(second.Disposed);
    }

    [Fact]
    public async Task FailedPreparationCanBeRetiredAndRepreparedWithoutPublishingAHalfInstance()
    {
        var created = 0;
        var native = new NativeOcr();
        using var factory = new OcrFactory(NullLogger<BgiOnnxFactory>.Instance,
            () => Interlocked.Increment(ref created) == 1 ? throw new InvalidOperationException("native construction failed") : native);
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.PrepareAsync());
        await factory.Unload();
        await factory.PrepareAsync();
        using var mat = new Mat(1, 1, MatType.CV_8UC3);
        Assert.Equal("recognized", factory.PaddleOcr.Ocr(mat));
        Assert.Equal(2, created);
    }

    [Fact]
    public async Task FailedRetirementKeepsTheOldInstanceUnavailableUntilItsReleaseSucceeds()
    {
        var native = new NativeOcr { RemainingDisposeFailures = 1 };
        using var factory = new OcrFactory(NullLogger<BgiOnnxFactory>.Instance, () => native);
        await factory.PrepareAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.Unload());
        await Assert.ThrowsAsync<InvalidOperationException>(() => factory.PrepareAsync());
        Assert.False(native.Disposed);
        await factory.Unload();
        Assert.True(native.Disposed);
        Assert.Equal(1, native.DisposeCount);
    }

    [Fact]
    public async Task LoggingFailureCannotLoseTheCreatedNativeInstanceOrPreventItsDisposal()
    {
        var native = new NativeOcr();
        using var factory = new OcrFactory(new ThrowingLogger(), () => native);
        await factory.PrepareAsync();
        await factory.Unload();
        Assert.Equal(1, native.DisposeCount);
    }

    [Fact]
    public async Task AReleasedFacadeCannotCreateAnotherEngineAfterItsFactoryIsDisposed()
    {
        var native = new NativeOcr();
        var factory = new OcrFactory(NullLogger<BgiOnnxFactory>.Instance, () => native);
        var facade = factory.PaddleOcr;
        await factory.PrepareAsync();
        await factory.DisposeAsync();
        using var mat = new Mat(1, 1, MatType.CV_8UC3);
        Assert.Throws<ObjectDisposedException>(() => facade.Ocr(mat));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => factory.PrepareAsync());
        Assert.Equal(1, native.DisposeCount);
    }

    [Fact]
    public async Task UnloadWaitsForTheBorrowedNativeOcrCallBeforeDisposingItsEngine()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var native = new NativeOcr(() => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); });
        using var factory = new OcrFactory(NullLogger<BgiOnnxFactory>.Instance, () => native);
        using var mat = new Mat(1, 1, MatType.CV_8UC3);
        var recognition = Task.Run(() => factory.PaddleOcr.OcrWithoutDetector(mat));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var unload = factory.Unload();
        try
        {
            Assert.False(unload.IsCompleted);
            Assert.False(native.Disposed);
        }
        finally
        {
            release.Set();
            Assert.Equal("recognized", await recognition.WaitAsync(TimeSpan.FromSeconds(5)));
            await unload.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.True(native.Disposed);
    }

    private sealed class NativeOcr(Action? recognize = null) : IOcrService, IDisposable
    {
        public bool Disposed { get; private set; }
        public int DisposeCount { get; private set; }
        public int RemainingDisposeFailures { get; set; }
        public string Ocr(Mat mat) => OcrWithoutDetector(mat);
        public string OcrWithoutDetector(Mat mat) { recognize?.Invoke(); return "recognized"; }
        public OcrResult OcrResult(Mat mat) { recognize?.Invoke(); return new([]); }
        public void Dispose()
        {
            if (RemainingDisposeFailures-- > 0) throw new InvalidOperationException("native dispose failed");
            Disposed = true; DisposeCount++;
        }
    }

    private sealed class ThrowingLogger : ILogger<BgiOnnxFactory>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new InvalidOperationException("log unavailable");
    }

    private sealed class RecordingLogger(System.Collections.Concurrent.ConcurrentQueue<string> messages) : ILogger<BgiOnnxFactory>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}
