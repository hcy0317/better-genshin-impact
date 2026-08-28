using BetterGenshinImpact.GameTask.ArtifactAnalysis;
using BetterGenshinImpact.GameTask;
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
    private readonly SemaphoreSlim _executionGate = new(1, 1);
    private ArtifactHostRequestReader? _requestReader;
    private FileSystemWatcher? _watcher;
    private SynchronizationContext? _executionContext;
    private int _watching;

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
        logger.LogInformation("已监控网页圣遗物任务目录：{RequestRoot}", _requestRoot);
    }

    public async Task RunAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        var launch = await ReadStableRequestAsync(requestPath, cancellationToken);
        logger.LogInformation(
            "开始网页圣遗物任务 {Operation}，Job={JobId}",
            launch.Request.Operation,
            launch.Request.JobId);
        await coordinator.RunAsync(launch.Request, launch.RequestToken, cancellationToken);
        logger.LogInformation(
            "网页圣遗物任务 {Operation} 完成，Job={JobId}",
            launch.Request.Operation,
            launch.Request.JobId);
    }

    public async Task RunObservedAsync(string requestPath)
    {
        await _executionGate.WaitAsync();
        try
        {
            await RunAsync(requestPath);
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
            _executionGate.Release();
        }
    }

    public void QueueRequest(string requestPath)
    {
        if (!_activeRequests.TryAdd(requestPath, 0)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(100);
                var executionContext = _executionContext
                    ?? throw new InvalidOperationException("圣遗物宿主执行上下文尚未初始化");
                await ArtifactHostExecutionContext.RunAsync(
                    executionContext,
                    () => RunObservedAsync(requestPath));
            }
            finally
            {
                _activeRequests.TryRemove(requestPath, out _);
            }
        });
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
                return await RequestReader.ReadAsync(requestPath, cancellationToken);
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
        _watcher?.Dispose();
        _watcher = null;
        _executionContext = null;
        _executionGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
