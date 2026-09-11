using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask;

// Only the completed recovery path can create this signal. It is not victory:
// the caller must retry the original route/commission within its existing budget.
internal sealed class CombatRecoveryCompletedException : RetryException
{
    private CombatRecoveryCompletedException() : base("[BGI_RECOVERY_COMPLETED] 神像恢复流程已完成并确认返回主界面，请重试原路线或委托") { }

    internal static async Task RecoverAsync(Func<Task> recover, Func<bool> confirm,
        Func<Task> waitForFreshFrame, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        await recover();
        ct.ThrowIfCancellationRequested();
        if (!confirm()) throw new InvalidOperationException("神像恢复后未确认主界面，不允许继续路线");
        await waitForFreshFrame();
        ct.ThrowIfCancellationRequested();
        if (!confirm()) throw new InvalidOperationException("神像恢复后主界面确认不稳定，不允许继续路线");
        TaskExecutionScope.ThrowIfFailed();
        throw new CombatRecoveryCompletedException();
    }
}
