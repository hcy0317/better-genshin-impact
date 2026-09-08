using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask;

internal sealed class TaskFailureRecoveryException : AggregateException
{
    internal TaskFailureRecoveryException(Exception taskFailure, Exception recoveryFailure)
        : base("任务失败且自动化状态恢复失败，已停止后续任务。", taskFailure, recoveryFailure)
    {
    }
}

internal static class TaskFailureRecoveryPolicy
{
    internal static bool IsRecoveryFailure(Exception failure)
    {
        if (failure is TaskFailureRecoveryException) return true;
        if (failure is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions)
                if (IsRecoveryFailure(inner)) return true;
        return failure.InnerException != null && IsRecoveryFailure(failure.InnerException);
    }

    internal static async Task RecoverOrThrowAsync(
        Exception taskFailure,
        Func<Task> recoverAsync,
        CancellationToken ct = default,
        ILogger? logger = null,
        TimeSpan? budget = null,
        Action<Exception, string>? captureFailure = null)
    {
        if (taskFailure is OperationCanceledException or NormalEndException || IsRecoveryFailure(taskFailure)
            || TaskExecutionScope.IsUnconfirmedCombat(taskFailure))
        {
            ExceptionDispatchInfo.Capture(taskFailure).Throw();
        }

        try
        {
            TaskExecutionScope.ThrowIfFailed();
            // 父级UI截止时间耗尽是恢复失败，不是用户取消；保留原始任务异常。
            UiOperation.Current?.Check();
            ct.ThrowIfCancellationRequested();
            await UiOperation.RunAsync("failure-recovery", budget ?? TimeSpan.FromSeconds(20), ct, async operation =>
            {
                try { captureFailure?.Invoke(taskFailure, $"UI root={operation.RootId} op={operation.Id} 原始失败，恢复前现场"); }
                catch { /* 现场保存失败不能替换原始异常或阻止安全恢复。 */ }
                await recoverAsync();
                return true;
            }, logger);
        }
        catch (Exception recoveryFailure) when (
            recoveryFailure is not OperationCanceledException and not NormalEndException)
        {
            throw new TaskFailureRecoveryException(taskFailure, recoveryFailure);
        }
    }
}
