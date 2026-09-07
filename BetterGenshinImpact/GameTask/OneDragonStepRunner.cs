using System;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask;

/// <summary>报告模式不影响安全边界：在任务锁内恢复，恢复失败或取消时停止一条龙。</summary>
internal static class OneDragonStepRunner
{
    internal static async Task<Exception?> RunAsync(bool reportFailure,
        Func<Func<Task>, bool, Task> runOwned, Func<Task> action, Func<Exception, Task> recover)
    {
        Exception? recoveredFailure = null;
        try
        {
            // 两种入口都必须看到异常；否则底层 runner 会先吞错，跳过恢复。
            await runOwned(async () =>
            {
                try { await action(); }
                catch (Exception error) when (error is not OperationCanceledException and not NormalEndException
                    && !TaskFailureRecoveryPolicy.IsRecoveryFailure(error))
                {
                    await recover(error);
                    recoveredFailure = error;
                    // 即使恢复成功，原任务仍需由 runner 记录为失败。
                    throw;
                }
            }, true);
        }
        catch (Exception error) when (ReferenceEquals(error, recoveredFailure))
        {
            return reportFailure ? error : null;
        }

        // 带有清理失败的聚合异常不匹配上面的原异常，必须停止而不能当作恢复成功。
        return null;
    }
}
