using System;
using System.Threading;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.AutoDomain;

public partial class AutoDomainTask
{
    internal static bool ShouldExitAfterDomainReward(bool resinLastTurn, bool finalConfiguredRound) =>
        resinLastTurn || finalConfiguredRound;

    // 原生任务负责退出交接，不能等脚本finally退出后才暴露此前的主界面等待失败。
    internal static async Task CompleteDomainHandoffAsync(Func<Task<bool>> exitDomain,
        Func<Task<bool>> waitForMainUi, Func<Task> processArtifacts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!await exitDomain())
            throw new InvalidOperationException("未确认退出秘境，停止后续背包处理");
        ct.ThrowIfCancellationRequested();
        if (!await waitForMainUi())
            throw new InvalidOperationException("秘境任务结束后未确认主界面，停止后续背包处理");
        ct.ThrowIfCancellationRequested();
        await processArtifacts();
        ct.ThrowIfCancellationRequested();
    }
}
