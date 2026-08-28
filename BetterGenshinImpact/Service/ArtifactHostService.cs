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
using System.Text.Json;

namespace BetterGenshinImpact.Service;

public sealed class ArtifactHostService(
    ArtifactHostCoordinator coordinator,
    ILogger<ArtifactHostService> logger) : IDisposable
{
    private readonly string _requestRoot = Path.Combine(
        AppContext.BaseDirectory, "User", "launch-requests", "artifact-analysis");
    private readonly ConcurrentDictionary<string, byte> _activeRequests = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Task, byte> _backgroundTasks = new();
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private ArtifactHostRequestReader? _requestReader;
    private FileSystemWatcher? _watcher;
    private SynchronizationContext? _executionContext;
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
            EnableRaisingEvents = true
        };
        _watcher.Created += (_, args) => QueueRequest(args.FullPath);
        _watcher.Renamed += (_, args) => QueueRequest(args.FullPath);
        _watcher.Changed += (_, args) => QueueRequest(args.FullPath);
        foreach (var requestPath in Directory.EnumerateFiles(_requestRoot, "*.json")
                     .OrderBy(File.GetCreationTimeUtc))
        {
            QueueRequest(requestPath);
        }
        var consumedRoot = Path.Combine(_requestRoot, "consumed");
        if (Directory.Exists(consumedRoot))
        {
            foreach (var requestPath in Directory.EnumerateFiles(consumedRoot, "*.json")
                         .OrderBy(File.GetCreationTimeUtc))
            {
                QueueRequest(requestPath);
            }
        }
        logger.LogInformation("已监控网页圣遗物任务目录：{RequestRoot}", _requestRoot);
    }

    public async Task RunAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        var launch = await ReadStableRequestAsync(requestPath, cancellationToken);
        logger.LogInformation(
            "开始网页圣遗物任务 {Operation}，Job={JobId}",
            launch.Request.Operation,
            launch.Request.JobId);
        await coordinator.RunAsync(
            launch.Request,
            launch.RequestToken,
            cancellationToken,
            launch.Recovery);
        logger.LogInformation(
            "网页圣遗物任务 {Operation} 完成，Job={JobId}",
            launch.Request.Operation,
            launch.Request.JobId);
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
        Task? worker = null;
        worker = Task.Run(async () =>
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
                if (worker is not null) _backgroundTasks.TryRemove(worker, out _);
            }
        });
        _backgroundTasks.TryAdd(worker, 0);
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
        _shutdownCancellation.Cancel();
        if (!_activeRequests.IsEmpty)
        {
            CancellationContext.Instance.Cancel();
        }
        _watcher?.Dispose();
        _watcher = null;
        _executionContext = null;
        var workers = _backgroundTasks.Keys.ToArray();
        _ = Task.WhenAll(workers).ContinueWith(
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
