using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Recognition.ONNX;

internal sealed class OnnxInitializationTask<T> : IDisposable
{
    private readonly string _modelName;
    private readonly Func<T> _factory;
    private readonly ILogger _logger;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _pollInterval;
    private readonly Action<T>? _disposeValue;
    private readonly Lazy<Task<T>> _initialization;
    private int _disposed;
    private int _valueDisposed;

    internal OnnxInitializationTask(
        string modelName,
        Func<T> factory,
        ILogger logger,
        TimeSpan? heartbeatInterval = null,
        Action<T>? disposeValue = null)
    {
        _modelName = modelName;
        _factory = factory;
        _logger = logger;
        _disposeValue = disposeValue;
        _heartbeatInterval = heartbeatInterval ?? TimeSpan.FromSeconds(15);
        if (_heartbeatInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(heartbeatInterval),
                heartbeatInterval,
                "Heartbeat interval must be positive.");
        }

        _pollInterval = _heartbeatInterval < TimeSpan.FromSeconds(1)
            ? _heartbeatInterval
            : TimeSpan.FromSeconds(1);
        _initialization = new Lazy<Task<T>>(
            InitializeAsync,
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    internal T Value
    {
        get
        {
            ThrowIfDisposed();
            var value = _initialization.Value.GetAwaiter().GetResult();
            ThrowIfDisposed();
            return value;
        }
    }

    internal bool IsValueCreated =>
        _initialization.IsValueCreated && _initialization.Value.IsCompletedSuccessfully;

    internal async Task<T> GetValueAsync(CancellationToken ct)
    {
        ThrowIfDisposed();
        var value = await _initialization.Value.WaitAsync(ct).ConfigureAwait(false);
        ThrowIfDisposed();
        return value;
    }

    internal bool TryGetValue(out T value)
    {
        if (Volatile.Read(ref _disposed) == 0 && IsValueCreated)
        {
            value = _initialization.Value.Result;
            return true;
        }

        value = default!;
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0
            || !_initialization.IsValueCreated)
        {
            return;
        }

        _ = _initialization.Value.ContinueWith(
            static (task, state) => ((OnnxInitializationTask<T>)state!).DisposeValue(task.Result),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnRanToCompletion,
            TaskScheduler.Default);
    }

    private void DisposeValue(T value)
    {
        if (Interlocked.Exchange(ref _valueDisposed, 1) == 0)
        {
            _disposeValue?.Invoke(value);
        }
    }

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new ObjectDisposedException(nameof(OnnxInitializationTask<T>));
        }
    }

    private async Task<T> InitializeAsync()
    {
        var stopwatch = Stopwatch.StartNew();
        var progress = new OnnxInitializationProgress(_heartbeatInterval);
        _logger.LogInformation("[ONNX]开始初始化模型 {Model} 预测器。", _modelName);
        var initialization = Task.Run(_factory);

        try
        {
            while (!initialization.IsCompleted)
            {
                var completed = await Task.WhenAny(
                    initialization,
                    Task.Delay(_pollInterval, CancellationToken.None)).ConfigureAwait(false);
                if (completed == initialization)
                {
                    break;
                }

                var observation = progress.Observe(stopwatch.Elapsed);
                if (observation.ShouldLog)
                {
                    _logger.LogInformation(
                        "[ONNX]模型 {Model} 仍在初始化，已等待 {ElapsedSeconds} 秒。首次 TensorRT 引擎构建可能较慢。",
                        _modelName,
                        observation.ElapsedSeconds);
                }
            }

            var value = await initialization.ConfigureAwait(false);
            _logger.LogInformation(
                "[ONNX]模型 {Model} 预测器初始化完成，耗时 {ElapsedMilliseconds} ms。",
                _modelName,
                stopwatch.ElapsedMilliseconds);
            return value;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "[ONNX]模型 {Model} 预测器初始化失败，耗时 {ElapsedMilliseconds} ms。",
                _modelName,
                stopwatch.ElapsedMilliseconds);
            throw;
        }
    }
}
