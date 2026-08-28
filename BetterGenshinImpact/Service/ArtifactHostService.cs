using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Core.Script;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using System.Text.Json;

namespace BetterGenshinImpact.Service;

public sealed class ArtifactHostService(
    ArtifactHostCoordinator coordinator,
    ILogger<ArtifactHostService> logger) : IDisposable
{
    private readonly string _requestRoot = Path.Combine(
        AppContext.BaseDirectory, "User", "launch-requests", "artifact-analysis");
    private readonly ConcurrentDictionary<string, byte> _activeRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly Channel<string> _requestQueue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    private ArtifactHostRequestReader? _requestReader;
    private FileSystemWatcher? _watcher;
    private SynchronizationContext? _executionContext;
    private Task? _queueWorker;
    private int _watching;
    private int _disposed;

    private ArtifactHostRequestReader RequestReader => _requestReader ??= new ArtifactHostRequestReader(_requestRoot);

    public void StartWatching()
    {
        if (Interlocked.Exchange(ref _watching, 1) != 0) return;
        _executionContext = ArtifactHostExecutionContext.CaptureApplicationContext();
        Directory.CreateDirectory(_requestRoot);
        _watcher = new FileSystemWatcher(_requestRoot, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            IncludeSubdirectories = false,
            EnableRaisingEvents = false
        };
        _watcher.Created += (_, args) => QueueRequest(args.FullPath);
        _watcher.Renamed += (_, args) => QueueRequest(args.FullPath);
        _watcher.Changed += (_, args) => QueueRequest(args.FullPath);
        _queueWorker = Task.Run(ProcessQueueAsync);
        var consumedRoot = Path.Combine(_requestRoot, "consumed");
        if (Directory.Exists(consumedRoot))
        {
            foreach (var requestPath in Directory.EnumerateFiles(consumedRoot, "*.json")
                         .OrderBy(File.GetCreationTimeUtc))
            {
                QueueRequest(requestPath);
            }
        }
        foreach (var requestPath in Directory.EnumerateFiles(_requestRoot, "*.json")
                     .OrderBy(File.GetCreationTimeUtc))
        {
            QueueRequest(requestPath);
        }
        _watcher.EnableRaisingEvents = true;
        // Close the enumeration-to-watcher gap; active-path deduplication keeps
        // files already queued above from running twice.
        foreach (var requestPath in Directory.EnumerateFiles(_requestRoot, "*.json")
                     .OrderBy(File.GetCreationTimeUtc))
        {
            QueueRequest(requestPath);
        }
        logger.LogInformation("已监控网页圣遗物任务目录：{RequestRoot}", _requestRoot);
    }

    public async Task RunAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var launch = await ReadStableRequestAsync(requestPath, cancellationToken);
            logger.LogInformation(
                "开始网页圣遗物任务 {Operation}，Job={JobId}，尝试 {Attempt}/3",
                launch.Request.Operation,
                launch.Request.JobId,
                attempt);
            try
            {
                await coordinator.RunAsync(
                    launch.Request,
                    launch.RequestToken,
                    cancellationToken,
                    launch.Recovery);
                logger.LogInformation(
                    "网页圣遗物任务 {Operation} 完成，Job={JobId}",
                    launch.Request.Operation,
                    launch.Request.JobId);
                return;
            }
            catch (ArtifactForwardRecoveryRequiredException exception) when (attempt < 3)
            {
                logger.LogWarning(
                    exception,
                    "原神方案前向恢复尚未收敛，将重试同一目标方案");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
    }

    public async Task RunObservedAsync(
        string requestPath,
        CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdownCancellation.Token);
        var entered = false;
        try
        {
            await _executionGate.WaitAsync(linked.Token);
            entered = true;
            await RunAsync(requestPath, linked.Token);
        }
        catch (OperationCanceledException exception)
        {
            logger.LogInformation(exception, "网页圣遗物任务已取消");
        }
        catch (Exception exception)
        {
            TaskFailureDiagnostics.CaptureScreenshotOnce(exception, "网页圣遗物任务执行失败");
            logger.LogError(exception, "网页圣遗物任务执行失败");
        }
        finally
        {
            if (entered) _executionGate.Release();
        }
    }

    public void QueueRequest(string requestPath)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (!_activeRequests.TryAdd(requestPath, 0)) return;
        if (!_requestQueue.Writer.TryWrite(requestPath))
        {
            _activeRequests.TryRemove(requestPath, out _);
        }
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var requestPath in _requestQueue.Reader.ReadAllAsync(
                               _shutdownCancellation.Token))
            {
                try
                {
                    await Task.Delay(100, _shutdownCancellation.Token);
                    var executionContext = _executionContext
                        ?? throw new InvalidOperationException("圣遗物宿主执行上下文尚未初始化");
                    await ArtifactHostExecutionContext.RunAsync(
                        executionContext,
                        () => RunObservedAsync(requestPath, _shutdownCancellation.Token));
                }
                catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
                {
                    logger.LogInformation("圣遗物宿主关闭，已取消排队请求：{RequestPath}", requestPath);
                }
                finally
                {
                    _activeRequests.TryRemove(requestPath, out _);
                }
            }
        }
        catch (OperationCanceledException) when (_shutdownCancellation.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task<ArtifactHostLaunchRequest> ReadStableRequestAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 6; attempt++)
        {
            try
            {
                var allowExpiredClaimed = string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(requestPath)),
                    "consumed",
                    StringComparison.OrdinalIgnoreCase);
                return await RequestReader.ReadAsync(
                    requestPath,
                    cancellationToken,
                    allowExpiredClaimed);
            }
            catch (Exception exception) when (
                exception is IOException or JsonException
                || exception is InvalidOperationException invalid
                && (invalid.Message.Contains("does not exist", StringComparison.Ordinal)
                    || invalid.Message.Contains("empty", StringComparison.Ordinal)))
            {
                lastFailure = exception;
                if (attempt < 6)
                {
                    await Task.Delay(attempt * 100, cancellationToken);
                }
            }
        }
        throw new InvalidOperationException(
            "圣遗物宿主请求文件在有界等待后仍未稳定可读。",
            lastFailure);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _requestQueue.Writer.TryComplete();
        _shutdownCancellation.Cancel();
        if (!_activeRequests.IsEmpty)
        {
            CancellationContext.Instance.Cancel();
        }
        _watcher?.Dispose();
        _watcher = null;
        _executionContext = null;
        var queueWorker = _queueWorker ?? Task.CompletedTask;
        _ = queueWorker.ContinueWith(
            _ =>
            {
                _executionGate.Dispose();
                _shutdownCancellation.Dispose();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        GC.SuppressFinalize(this);
    }
}
