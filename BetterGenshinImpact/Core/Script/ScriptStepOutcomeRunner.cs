using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.Core.Script;

internal readonly record struct ScriptStepOutcome(ScriptExecutionResult Outcome, Exception? RecoveredFailure);

/// <summary>在调用方已经持有的任务锁内，保留子结果并完成继续执行所需的UI恢复。</summary>
internal static class ScriptStepOutcomeRunner
{
    internal static async Task<ScriptStepOutcome> RunAsync(Func<Task<ScriptExecutionResult>> execute,
        Func<Exception, Task> recover, CancellationToken ct, ILogger? logger = null,
        Action<Exception, string>? captureFailure = null, TimeSpan? recoveryBudget = null,
        Func<CancellationToken, Task>? verifyNetworkRestart = null)
    {
        ct.ThrowIfCancellationRequested();
        TaskExecutionScope.BeginScriptReplayBoundary();
        for (var restart = 0; ; restart++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (restart > 0 && verifyNetworkRestart != null) await verifyNetworkRestart(ct);
                return await RunAttemptAsync(execute, recover, ct, logger, captureFailure, recoveryBudget);
            }
            catch (NetworkTaskRetryException error) when (restart < 2)
            {
                TaskExecutionScope.EnsureNetworkReplaySafe(error.InnerException ?? error);
                TaskExecutionScope.ConsumeNetworkRetry();
                try { logger?.LogWarning("NETWORK_TASK_RETRY attempt={Attempt}/2; 重连已确认，仅重跑当前脚本，保留配置组已完成脚本", restart + 1); }
                catch { /* Diagnostics cannot replace the verified restart boundary. */ }
            }
        }
    }

    private static async Task<ScriptStepOutcome> RunAttemptAsync(Func<Task<ScriptExecutionResult>> execute,
        Func<Exception, Task> recover, CancellationToken ct, ILogger? logger,
        Action<Exception, string>? captureFailure, TimeSpan? recoveryBudget)
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
        catch (Exception error) when (error is not NetworkTaskRetryException && !TaskFailureRecoveryPolicy.IsTerminalFailure(error))
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
