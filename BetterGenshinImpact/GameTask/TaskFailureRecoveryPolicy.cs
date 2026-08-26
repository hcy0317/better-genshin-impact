using System;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

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
    internal static async Task RecoverOrThrowAsync(
        Exception taskFailure,
        Func<Task> recoverAsync)
    {
        if (taskFailure is OperationCanceledException or NormalEndException)
        {
            ExceptionDispatchInfo.Capture(taskFailure).Throw();
        }

        try
        {
            await recoverAsync();
        }
        catch (Exception recoveryFailure) when (
            recoveryFailure is not OperationCanceledException and not NormalEndException)
        {
            throw new TaskFailureRecoveryException(taskFailure, recoveryFailure);
        }
    }
}
