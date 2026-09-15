using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Script;

namespace BetterGenshinImpact.GameTask.Common;

/// <summary>最外层运行权覆盖配置读取、游戏准备、全部子任务和结果收尾；不长期占用子任务输入锁。</summary>
internal static class AutomationRunCoordinator
{
    internal static async Task RunAsync(CancellationContext cancellation, TaskAdmissionGate admission,
        SemaphoreSlim input, Func<Task> work, Func<Exception?, bool, Task>? finalize = null)
    {
        CancellationContext.RunLease? run = null;
        IDisposable? activity = null;
        Exception? failure = null, finalizationFailure = null;
        var cancelled = false;
        try
        {
            admission.Check();
            if (!await input.WaitAsync(0)) throw new InvalidOperationException("已有独立任务运行，不能启动一条龙");
            try { admission.Initialize(() => run = cancellation.EnterRun()); }
            finally { input.Release(); }
            activity = admission.EnterActivity();
            await work();
        }
        catch (Exception error) { failure = error; }
        if (run != null)
        {
            cancelled = cancellation.IsCancellationRequested;
            try { await run.DrainAsync(); }
            catch (Exception error)
            {
                if (failure == null) failure = error;
                else if (!ReferenceEquals(failure, error)) failure.Data["RunCancellationCleanup"] = error;
            }
        }
        try
        {
            if (finalize != null) await finalize(failure, cancelled);
        }
        catch (Exception error) { finalizationFailure = error; }
        finally
        {
            try
            {
                if (run != null) await run.DisposeAsync();
            }
            catch (Exception error)
            {
                if (failure == null) failure = error;
                else if (!ReferenceEquals(failure, error)) failure.Data["RunRetirementCleanup"] = error;
            }
            finally { activity?.Dispose(); }
        }
        if (finalizationFailure != null)
        {
            if (failure != null) finalizationFailure.Data["RunFailure"] = failure;
            ExceptionDispatchInfo.Capture(finalizationFailure).Throw();
        }
        // finalizer负责把work/取消/清理失败写为明确结果；普通UI/CLI入口则保留原异常。
        if (finalize == null && failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
