using System;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Utils;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask;
using Microsoft.Extensions.Logging;
using Microsoft.ClearScript;

namespace BetterGenshinImpact.Core.Script.Dependence;

/// <summary>仅在原生路线完成且任务边界仍健康时发布；失败和取消仍抛出原异常。</summary>
public sealed class PathingRunResult
{
    internal PathingRunResult() { }

    [ScriptMember("success")]
    public bool Success => true;
}

public class AutoPathingScript
{
    private object? _config = null;
    private string _rootPath;
    private readonly LimitedFile _autoPathingFile;
    private readonly Action<string, Exception> _logFailure;
    private readonly Func<string, string?, Task<bool>> _executePath;
    private readonly TaskExecutionScope.Guard _taskGuard = TaskExecutionScope.Capture();
    private readonly Action<Exception, string> _captureFailure;

    public AutoPathingScript(string rootPath, object? config)
        : this(rootPath, config, new LimitedFile(Global.Absolute(@"User\AutoPathing")), LogFailure)
    {
    }

    internal AutoPathingScript(
        string rootPath,
        object? config,
        LimitedFile autoPathingFile,
        Action<string, Exception> logFailure,
        Func<string, string?, Task<bool>>? executePath = null,
        Action<Exception, string>? captureFailure = null)
    {
        _config = config;
        _rootPath = rootPath;
        _autoPathingFile = autoPathingFile;
        _logFailure = logFailure;
        _executePath = executePath ?? ExecuteNativePath;
        _captureFailure = captureFailure ?? ((error, context) => TaskFailureDiagnostics.CaptureScreenshotOnce(error, context));
    }

    /// <summary>
    /// 获取当前脚本任务是否已收到取消请求；终止故障保持原异常，不能被脚本当普通路线失败继续。
    /// </summary>
    public bool IsCancellationRequested
    {
        get
        {
            _taskGuard.Check();
            return CancellationContext.Instance.IsCancellationRequested;
        }
    }

    public async Task<PathingRunResult> Run(string json)
    {
        return await Run(json, null);
    }

    private async Task<PathingRunResult> Run(string json, string? sourcePath)
    {
        using var owned = _taskGuard.Enter();
        try
        {
            if (!await _executePath(json, sourcePath))
                throw new InvalidOperationException("地图追踪未完整完成，不能将本路线记为成功或写入采集冷却");
            _taskGuard.Check();
            return new PathingRunResult();
        }
        catch (Exception e)
        {
            _taskGuard.Report(e);
            var context = string.IsNullOrWhiteSpace(sourcePath)
                ? "地图追踪执行失败"
                : $"地图追踪执行失败-{System.IO.Path.GetFileName(sourcePath)}";
            _captureFailure(e, context);
            _logFailure("执行地图追踪时候发生错误", e);
            throw;
        }
    }

    private async Task<bool> ExecuteNativePath(string json, string? sourcePath)
    {
        var task = string.IsNullOrEmpty(sourcePath)
            ? PathingTask.BuildFromJson(json)
            : PathingTask.BuildFromJson(json, sourcePath);
        var pathExecutor = new PathExecutor(CancellationContext.Instance.Cts.Token);
        if (_config is PathingPartyConfig partyConfig) pathExecutor.PartyConfig = partyConfig;
        await pathExecutor.Pathing(task);
        return pathExecutor.SuccessEnd;
    }

    public async Task<PathingRunResult> RunFile(string path)
    {
        string json;
        try
        {
            json = await new LimitedFile(_rootPath).ReadTextOrThrow(path);
        }
        catch (Exception e)
        {
            _logFailure("读取文件时发生错误", e);
            throw;
        }

        return await Run(json, ScriptUtils.NormalizePath(_rootPath, path));
    }

    /// <summary>
    /// 从已订阅的内容中获取文件
    /// </summary>
    /// <param name="path">在 `\User\AutoPathing` 目录下获取文件</param>
    public async Task<PathingRunResult> RunFileFromUser(string path)
    {
        var json = await AutoPathingFile.ReadTextOrThrow(path);
        return await Run(json, ScriptUtils.NormalizePath(Global.Absolute(@"User\AutoPathing"), path));
    }

    /// <summary>
    /// 判断 AutoPathing 目录下的路径是否存在
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的路径</param>
    /// <returns>存在返回 true，否则返回 false</returns>
    public bool IsExists(string subPath) => AutoPathingFile.IsExists(subPath);

    /// <summary>
    /// 判断 AutoPathing 目录下的路径是否为文件
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的路径</param>
    /// <returns>是文件返回 true，否则返回 false</returns>
    public bool IsFile(string subPath) => AutoPathingFile.IsFile(subPath);

    /// <summary>
    /// 判断 AutoPathing 目录下的路径是否为文件夹
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的路径</param>
    /// <returns>是文件夹返回 true，否则返回 false</returns>
    public bool IsFolder(string subPath) => AutoPathingFile.IsFolder(subPath);

    /// <summary>
    /// 读取 AutoPathing 目录下指定文件夹的内容（非递归方式）
    /// 目录不存在时返回空数组，不会自动创建目录
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的子目录路径，默认为相对根目录</param>
    /// <returns>文件夹内所有文件和文件夹的相对路径数组，出错时返回空数组</returns>
    public string[] ReadPathSync(string subPath = "./") => AutoPathingFile.ReadPathSync(subPath);

    /// <summary>
    /// 读取 AutoPathing 目录下指定文件的文本内容
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的文件路径</param>
    /// <returns>文件文本内容，读取失败时返回空字符串</returns>
    public string ReadTextSync(string subPath) => AutoPathingFile.ReadTextSync(subPath);

    /// <summary>
    /// 读取 AutoPathing 目录下指定文件的文本内容，读取失败时保留原始异常。
    /// </summary>
    /// <param name="subPath">相对于 User\AutoPathing 的文件路径</param>
    /// <returns>文件文本内容</returns>
    public string ReadTextSyncOrThrow(string subPath) => AutoPathingFile.ReadTextSyncOrThrow(subPath);

    /// <summary>
    /// LimitedFile 实例，用于操作 AutoPathing 目录
    /// </summary>
    private LimitedFile AutoPathingFile => _autoPathingFile;

    private static void LogFailure(string message, Exception exception)
    {
        TaskControl.Logger.LogDebug(exception, message);
        TaskControl.Logger.LogError("{Message}: {ExceptionMessage}", message, exception.Message);
    }
}
