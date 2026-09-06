using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>宿主保有会话 CTS；本组在返回前等待战斗、结束检测和辅助任务全部退出。</summary>
internal static class NativeCombatTaskGroup
{
    internal static async Task RunAsync(
        CancellationTokenSource session,
        CancellationToken parentToken,
        Func<Task> combat,
        Func<Task> detectEnd,
        Func<Task>? assistant = null)
    {
        var combatTask = StartChild(combat, endSessionOnSuccess: true);
        var detectorTask = StartChild(detectEnd, endSessionOnSuccess: true);
        var assistantTask = assistant == null
            ? Task.CompletedTask
            : StartChild(assistant, endSessionOnSuccess: false);

        await Task.WhenAll(combatTask, detectorTask, assistantTask).ConfigureAwait(false);
        parentToken.ThrowIfCancellationRequested();

        Task StartChild(Func<Task> operation, bool endSessionOnSuccess) => Task.Run(async () =>
        {
            var endSession = endSessionOnSuccess;
            try
            {
                // 不把已取消令牌交给调度器，确保包装任务总会运行并进入统一退出边界。
                session.Token.ThrowIfCancellationRequested();
                await operation().ConfigureAwait(false);
            }
            catch (Exception exception) when (session.IsCancellationRequested
                && (exception is OperationCanceledException or NormalEndException))
            {
                // 检测完成后的兄弟取消属于正常收尾；用户取消由 parentToken 单独保留。
            }
            catch
            {
                endSession = true;
                throw;
            }
            finally
            {
                if (endSession) await session.CancelAsync().ConfigureAwait(false);
            }
        });
    }
}
