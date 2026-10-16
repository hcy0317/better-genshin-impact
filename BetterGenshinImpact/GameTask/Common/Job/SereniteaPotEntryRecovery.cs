using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.GameTask.Common.Job;

internal static class SereniteaPotEntryRecovery
{
    internal static readonly TimeSpan DiscoveryBudget = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan ArrivalBudget = TimeSpan.FromSeconds(45);
    // Two already permitted entry attempts plus the existing statue/UI handoff envelope.
    // One absolute deadline owns the whole transaction; nested operations can only shorten it.
    internal static readonly TimeSpan TotalBudget = (DiscoveryBudget + ArrivalBudget) * 2 + UiHandoffRecovery.Budget;

    internal static Task<bool> EnterAsync(Func<CancellationToken, Task<bool>> enter,
        Func<CancellationToken, Task> leaveCombat, CancellationToken ct, TimeProvider? clock = null) =>
        UiOperation.RunAsync("serenitea-pot-entry", TotalBudget, ct,
            operation => EnterCoreAsync(enter, leaveCombat, operation), clock: clock);

    private static async Task<bool> EnterCoreAsync(Func<CancellationToken, Task<bool>> enter,
        Func<CancellationToken, Task> leaveCombat, UiOperation operation)
    {
        var ct = operation.Token;
        void Check()
        {
            operation.Check();
        }
        Check();
        try { return await enter(ct); }
        catch (TeleportCombatBlockedException)
        {
            // 只处理游戏明确拒绝；Unknown回执、普通超时和任务停止不准入重试。
            Check();
            await leaveCombat(ct);
            Check();
            return await enter(ct); // 第二次拒绝直接传播，不循环扩大恢复预算。
        }
    }
}
