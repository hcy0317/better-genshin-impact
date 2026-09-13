using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.AutoBoss;

internal sealed class BossRewardUncertainException(string message, Exception? inner = null)
    : InvalidOperationException("[BGI_REWARD_NEEDS_RECONCILE] " + message, inner);

/// <summary>一次Start的循环和消费进度；重试只重建游戏环境，不重置已确认领奖份额。</summary>
internal sealed class BossRunProgress(int? targetClaims, int maximumRetries)
{
    internal int CompletedClaims { get; private set; }
    internal bool ClaimPending { get; private set; }
    internal bool HasRemaining => targetClaims == null || CompletedClaims < targetClaims;

    internal void BeginClaim()
    {
        if (ClaimPending) throw new BossRewardUncertainException("上一次领奖消费尚未确认，禁止再次消费树脂");
        if (!HasRemaining) throw new InvalidOperationException("已达到本次任务领奖上限");
        ClaimPending = true;
    }
    internal void ConfirmClaim()
    {
        if (!ClaimPending) throw new InvalidOperationException("不存在待确认的领奖输入");
        ClaimPending = false;
        CompletedClaims++;
    }
    internal void ConfirmNoConsumption() => ClaimPending = false;

    internal async Task RunAsync(Func<Task> prepare, Func<Task<bool>> round, Func<Task> betweenRounds,
        Func<int, RetryException, Task> retry, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumRetries);
        var retries = 0;
        var needsPreparation = true;
        while (HasRemaining)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (needsPreparation) { await prepare(); needsPreparation = false; }
                var before = CompletedClaims;
                var proceed = await round();
                ct.ThrowIfCancellationRequested();
                if (ClaimPending) throw new BossRewardUncertainException("领奖结束但未确认消费结果");
                if (!proceed) return;
                if (CompletedClaims != before + 1) throw new InvalidOperationException("本轮没有且仅有一次确认领奖");
                if (HasRemaining) await betweenRounds();
            }
            catch (RetryException error)
            {
                ct.ThrowIfCancellationRequested();
                if (ClaimPending) throw new BossRewardUncertainException("失败发生在消费输入之后，需先复核奖励", error);
                if (!HasRemaining || retries >= maximumRetries) throw;
                await retry(++retries, error);
                needsPreparation = true;
            }
        }
    }
}
