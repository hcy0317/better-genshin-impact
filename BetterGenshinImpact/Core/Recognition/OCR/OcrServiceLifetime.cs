using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;

namespace BetterGenshinImpact.Core.Recognition.OCR;

internal enum OcrServiceState { Uninitialized, Preparing, Ready, Failed, Retiring, Disposed }
internal readonly record struct OcrLifecycleObservation(long Generation, OcrServiceState State, double Milliseconds, int Borrowers, int Suppressed);

/// <summary>稳定的OCR门面；每次调用借用同一代native实例，退役先拒入、等构造和借用结束再释放。</summary>
internal sealed class OcrServiceLifetime : IDisposable, IAsyncDisposable
{
    private sealed record BorrowScope(OcrServiceLifetime Owner, BorrowScope? Parent);
    private static readonly AsyncLocal<BorrowScope?> CurrentBorrow = new();
    private readonly object _gate = new();
    private readonly object _recognitionGate = new();
    private readonly Func<IOcrService> _create;
    private readonly Action<OcrLifecycleObservation>? _observe;
    private Task<IOcrService>? _initialization;
    private IOcrService? _instance;
    private Task? _retirement;
    private TaskCompletionSource? _drained;
    private bool _closed, _retiring;
    private int _borrowers;
    private long _generation;
    private int _diagnosticCount, _suppressedDiagnostics, _finalObservation;
    private OcrServiceState _state;

    internal OcrServiceLifetime(Func<IOcrService> create, Action<OcrLifecycleObservation>? observe = null)
    { _create = create; _observe = observe; Service = new BorrowedService(this); }

    internal IOcrService Service { get; }
    internal OcrServiceState State { get { lock (_gate) return _state; } }

    private void CheckAdmission()
    {
        ObjectDisposedException.ThrowIf(_closed, this);
        if (_retiring) throw new InvalidOperationException("OCR服务正在退役，不接受新的识别借用");
    }

    private Task<IOcrService> GetInitialization()
    {
        lock (_gate)
        {
            CheckAdmission();
            if (_initialization != null) return _initialization;
            _retirement = null;
            _state = OcrServiceState.Preparing;
            var generation = ++_generation;
            _initialization = Task.Run(() =>
            {
                // 共享构造与预热不属于首个等待者的UI预算。
                using var execution = new RecognitionExecutionScope(CancellationToken.None);
                using var preparation = new RecognitionReadinessScope(nonBlocking: false);
                var started = Stopwatch.GetTimestamp();
                Observe(generation, OcrServiceState.Preparing, started);
                try
                {
                    var instance = _create() ?? throw new InvalidOperationException("OCR构造没有返回实例");
                    lock (_gate)
                    {
                        // 即便等待者已取消或Unload已开始，这一代也保留对象直至其退役任务回收。
                        _instance = instance;
                        if (!_retiring) _state = OcrServiceState.Ready;
                    }
                    Observe(generation, _retiring ? OcrServiceState.Retiring : OcrServiceState.Ready, started);
                    return instance;
                }
                catch
                {
                    lock (_gate) if (!_retiring) _state = OcrServiceState.Failed;
                    Observe(generation, OcrServiceState.Failed, started);
                    throw;
                }
            });
            // 所有等待者取消时也观察native构造的失败；不取消或遗弃它。
            _ = _initialization.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return _initialization;
        }
    }

    internal async Task PrepareAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var initialization = GetInitialization();
        await initialization.WaitAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            CheckAdmission();
            if (!ReferenceEquals(_initialization, initialization)) throw new InvalidOperationException("OCR准备的代际已经退役");
        }
    }

    private T Use<T>(Func<IOcrService, T> recognize)
    {
        RecognitionExecutionScope.Token.ThrowIfCancellationRequested();
        Task<IOcrService> initialization;
        if (RecognitionReadinessScope.IsNonBlocking)
        {
            lock (_gate)
            {
                if (_closed || _retiring || _initialization?.IsCompletedSuccessfully != true)
                    throw new RecognitionNotReadyException("OCR模型未就绪，当前观察保持未知");
                initialization = _initialization;
            }
        }
        else initialization = GetInitialization();
        var token = RecognitionExecutionScope.Token;
        token.ThrowIfCancellationRequested();
        var instance = initialization.WaitAsync(token).GetAwaiter().GetResult(); // 只取消等待，不取消共享初始化。
        var entered = false;
        try
        {
            if (RecognitionReadinessScope.IsNonBlocking)
            {
                if (!System.Threading.Monitor.TryEnter(_recognitionGate)) throw new RecognitionNotReadyException("OCR正在被借用，当前观察不等待预测锁");
                entered = true;
            }
            else
            {
                while (!System.Threading.Monitor.TryEnter(_recognitionGate, 25)) token.ThrowIfCancellationRequested();
                entered = true;
            }
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                CheckAdmission();
                if (!ReferenceEquals(_initialization, initialization) || !ReferenceEquals(instance, _instance))
                    throw new InvalidOperationException("OCR实例已经退役");
                _borrowers++;
            }
            var previous = CurrentBorrow.Value;
            CurrentBorrow.Value = new(this, previous);
            try { return recognize(instance); }
            finally
            {
                CurrentBorrow.Value = previous;
                lock (_gate) if (--_borrowers == 0) _drained?.TrySetResult();
            }
        }
        finally { if (entered) System.Threading.Monitor.Exit(_recognitionGate); }
    }

    internal Task UnloadAsync() => RetireAsync(close: false);

    private Task RetireAsync(bool close)
    {
        lock (_gate)
        {
            _closed |= close;
            if (_retirement != null && !_retirement.IsFaulted && !_retirement.IsCanceled)
            {
                if (_closed && _retirement.IsCompletedSuccessfully)
                {
                    _state = OcrServiceState.Disposed;
                    Observe(_generation, _state, Stopwatch.GetTimestamp());
                }
                return _retirement;
            }
            _retiring = true;
            _state = OcrServiceState.Retiring;
            var initialization = _initialization;
            var drained = _borrowers == 0 ? Task.CompletedTask : (_drained ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            var generation = _generation;
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _retirement = completion.Task;
            _ = Task.Run(async () =>
            {
                var started = Stopwatch.GetTimestamp();
                Observe(generation, OcrServiceState.Retiring, started);
                try
                {
                    if (initialization != null)
                        try { await initialization.ConfigureAwait(false); }
                        catch { /* 构造失败已交给原等待者；没有成功发布的native对象仍需结束这一代。 */ }
                    await drained.ConfigureAwait(false);
                    IOcrService? instance;
                    lock (_gate) instance = _instance;
                    (instance as IDisposable)?.Dispose();
                    OcrServiceState state;
                    lock (_gate)
                    {
                        _instance = null;
                        _initialization = null;
                        _drained = null;
                        _retiring = false;
                        state = _state = _closed ? OcrServiceState.Disposed : OcrServiceState.Uninitialized;
                    }
                    Observe(generation, state, started);
                    completion.TrySetResult();
                }
                catch (Exception error)
                {
                    // 释放失败保留这一代及关闭准入；下一次Unload可重试，不伪造释放成功。
                    Observe(generation, OcrServiceState.Retiring, started);
                    completion.TrySetException(error);
                }
            });
            return _retirement;
        }
    }

    private void Observe(long generation, OcrServiceState state, long started)
    {
        if (_observe == null) return;
        if (state == OcrServiceState.Disposed)
        {
            if (Interlocked.Exchange(ref _finalObservation, 1) != 0) return;
        }
        else if (Interlocked.Increment(ref _diagnosticCount) > 64)
        {
            Interlocked.Increment(ref _suppressedDiagnostics);
            return;
        }
        try { _observe(new(generation, state, Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Volatile.Read(ref _borrowers), Volatile.Read(ref _suppressedDiagnostics))); }
        catch { /* 诊断接收器不能改变实例归属或排空。 */ }
    }

    public ValueTask DisposeAsync() => new(RetireAsync(close: true));
    public void Dispose()
    {
        for (var borrow = CurrentBorrow.Value; borrow != null; borrow = borrow.Parent)
            if (ReferenceEquals(borrow.Owner, this)) throw new InvalidOperationException("OCR借用内部不能同步等待自身退役，请在调用结束后关闭");
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private sealed class BorrowedService(OcrServiceLifetime owner) : IOcrService
    {
        public string Ocr(Mat mat) => owner.Use(service => service.Ocr(mat));
        public string OcrWithoutDetector(Mat mat) => owner.Use(service => service.OcrWithoutDetector(mat));
        public OcrResult OcrResult(Mat mat) => owner.Use(service => service.OcrResult(mat));
    }
}
