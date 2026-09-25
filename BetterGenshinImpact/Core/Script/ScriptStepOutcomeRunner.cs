using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Core.Script;

internal readonly record struct ScriptStepOutcome(ScriptExecutionResult Outcome, Exception? RecoveredFailure);

/// <summary>在调用方已经持有的任务锁内，保留子结果并完成继续执行所需的UI恢复。</summary>
internal static class ScriptStepOutcomeRunner
{
    internal static async Task<ScriptStepOutcome> RunAsync(Func<Task<ScriptExecutionResult>> execute,
        Func<Exception, Task> recover, CancellationToken ct, ILogger? logger = null,
        Action<Exception, string>? captureFailure = null, TimeSpan? recoveryBudget = null)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.ThrowIfFailed();
        ScriptExecutionResult outcome;
        Exception? recoveredFailure = null;
        try
        {
            outcome = await execute();
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            if (outcome.Kind == ScriptOutcomeKind.Cancelled) outcome.ThrowIfFailure();
        }
        catch (Exception error) when (!TaskFailureRecoveryPolicy.IsTerminalFailure(error))
        {
            recoveredFailure = error;
            outcome = new(ScriptOutcomeKind.Failed, error.Message);
        }

        if (outcome.Kind is ScriptOutcomeKind.Deferred or ScriptOutcomeKind.NeedsReconcile or ScriptOutcomeKind.Failed)
        {
            var failure = recoveredFailure ?? new InvalidOperationException($"[BGI_SCRIPT_INCOMPLETE] {outcome.Kind}: {outcome.Reason}");
            await TaskFailureRecoveryPolicy.RecoverOrThrowAsync(failure, () => recover(failure), ct, logger,
                budget: recoveryBudget, captureFailure: captureFailure);
            ct.ThrowIfCancellationRequested();
            TaskExecutionScope.ThrowIfFailed();
            if (recoveredFailure != null && PathingTargetUnavailableException.IsUnavailableTarget(recoveredFailure))
                outcome = new(ScriptOutcomeKind.Skipped, "TARGET_UNAVAILABLE: " + recoveredFailure.Message);
        }
        return new(outcome, recoveredFailure);
    }
}
