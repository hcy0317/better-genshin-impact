using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.Common.Ui;
using OpenCvSharp;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR.Paddle;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.CoreTests.RecognitionTests.OCRTests;

[Collection("OfflineNativeDecision")]
public class OcrDeadlineTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task UiCancellationStopsOnlyInitializationWaiter(bool userCancellation)
    {
        var clock = new FakeTimeProvider();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var lifetime = new OcrServiceLifetime(() =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test release");
            RecognitionExecutionScope.Token.ThrowIfCancellationRequested();
            return new FakeOcr();
        });
        using var image = new Mat(1, 1, MatType.CV_8UC3);
        var caller = Task.Run(() => UiOperation.RunAsync("test-ocr", TimeSpan.FromSeconds(10), cancellation.Token,
            _ => Task.FromResult(lifetime.Service.Ocr(image)), clock: clock));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            if (userCancellation)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => caller.WaitAsync(TimeSpan.FromSeconds(1)));
            }
            else
            {
                clock.Advance(TimeSpan.FromSeconds(10));
                var failure = await Assert.ThrowsAsync<TimeoutException>(() => caller.WaitAsync(TimeSpan.FromSeconds(1)));
                Assert.Contains("界面转换超时：test-ocr", failure.Message);
            }
        }
        finally { release.Set(); await IgnoreFailure(caller); }
        await lifetime.PrepareAsync();
        Assert.Equal("recognized", lifetime.Service.Ocr(image));
        Assert.Null(System.Windows.Application.Current);
    }

    [OfflineNativeDecisionFact]
    public void CpuOrtCancellationIsPerRunAndSessionCanRunAgain()
    {
        using var factory = new BgiOnnxFactory(NullLogger<BgiOnnxFactory>.Instance, forceCpuOcr: true);
        using var session = factory.CreateInferenceSession(BgiOnnxModel.PaddleOcrDetV6, ocr: true);
        var input = new[] { NamedOnnxValue.CreateFromTensor(session.InputNames[0], new DenseTensor<float>(new[] { 1, 3, 64, 64 })) };
        using var cancellation = new CancellationTokenSource();
        using (var execution = new RecognitionExecutionScope(cancellation.Token))
            Assert.ThrowsAny<OperationCanceledException>(() => OcrInference.Run(options =>
            {
                // 取消在按次运行作用域建立之后发生，交给真实CPU ORT处理终止请求。
                cancellation.Cancel();
                using var result = session.Run(input, session.OutputNames, options);
                return result.Count;
            }));
        Assert.True(OcrInference.Run(options =>
        {
            using var result = session.Run(input, session.OutputNames, options);
            return result.Count > 0;
        }));
        Assert.Null(System.Windows.Application.Current);
    }

    [Fact]
    public void UnrelatedFailureIsNotReplacedByConcurrentCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var execution = new RecognitionExecutionScope(cancellation.Token);
        var original = new InvalidOperationException("shape mismatch");
        var failure = Assert.Throws<InvalidOperationException>(() => OcrInference.Run<int>(_ =>
        {
            cancellation.Cancel();
            throw original;
        }));
        Assert.Same(original, failure);
    }

    [Fact]
    public async Task CancelledLockWaitDoesNotReleaseOwnersLockOrEnterRecognition()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        using var lifetime = new OcrServiceLifetime(() => new FakeOcr(() =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("test release");
        }));
        await lifetime.PrepareAsync();
        using var image = new Mat(1, 1, MatType.CV_8UC3);
        var owner = Task.Run(() => lifetime.Service.Ocr(image));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
        var waiter = Task.Run(() => UiOperation.RunAsync("lock-wait", TimeSpan.FromSeconds(10), cancellation.Token,
            _ => Task.FromResult(lifetime.Service.Ocr(image))));
        try
        {
            await Task.Delay(50);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter.WaitAsync(TimeSpan.FromSeconds(1)));
            Assert.Equal(1, Volatile.Read(ref calls));
            Assert.False(owner.IsCompleted);
        }
        finally { release.Set(); await IgnoreFailure(waiter); await owner; }
        Assert.Equal("recognized", lifetime.Service.Ocr(image));
    }

    [Fact]
    public void NestedRecognitionScopeRestoresParentAndDoesNotChangeReadiness()
    {
        using var parent = new CancellationTokenSource();
        using var outer = new RecognitionExecutionScope(parent.Token);
        using var readiness = new RecognitionReadinessScope();
        using (new RecognitionExecutionScope(CancellationToken.None))
        {
            Assert.False(RecognitionExecutionScope.Token.CanBeCanceled);
            Assert.True(RecognitionReadinessScope.IsNonBlocking);
        }
        Assert.Equal(parent.Token, RecognitionExecutionScope.Token);
    }

    private static async Task IgnoreFailure(Task task) { try { await task; } catch { } }
    private sealed class FakeOcr(Action? recognize = null) : IOcrService
    {
        public string Ocr(Mat mat) { recognize?.Invoke(); return "recognized"; }
        public string OcrWithoutDetector(Mat mat) => Ocr(mat);
        public OcrResult OcrResult(Mat mat) => throw new NotSupportedException();
    }
}
