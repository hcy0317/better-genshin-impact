using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Recognition.ONNX;

internal sealed class OnnxInitializationTask<T>
{
    private readonly string _modelName;
    private readonly Func<T> _factory;
    private readonly ILogger _logger;
    private readonly TimeSpan _heartbeatInterval;
    private readonly TimeSpan _pollInterval;
    private readonly Lazy<Task<T>> _initialization;

    internal OnnxInitializationTask(
        string modelName,
        Func<T> factory,
        ILogger logger,
        TimeSpan? heartbeatInterval = null)
    {
        _modelName = modelName;
        _factory = factory;
        _logger = logger;
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

    internal T Value => _initialization.Value.GetAwaiter().GetResult();

    internal bool IsValueCreated =>
        _initialization.IsValueCreated && _initialization.Value.IsCompletedSuccessfully;

    internal async Task<T> GetValueAsync(CancellationToken ct)
    {
        return await _initialization.Value.WaitAsync(ct).ConfigureAwait(false);
    }

    internal bool TryGetValue(out T value)
    {
        if (IsValueCreated)
        {
            value = _initialization.Value.Result;
            return true;
        }

        value = default!;
        return false;
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
