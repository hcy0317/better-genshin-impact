namespace BetterGenshinImpact.GameTask;

/// <summary>
/// 一条龙任务失败后恢复连续失败的容忍预算：单次恢复失败只跳过该任务，
/// 只有连续多次失败才按"停止后续任务"处理。
/// </summary>
internal sealed class OneDragonRecoveryFailureBudget(int maxConsecutiveFailures = 2)
{
    internal int ConsecutiveFailures { get; private set; }

    /// <summary>记录一次恢复失败；返回 true 表示仍可跳过该任务并继续后续任务。</summary>
    internal bool TryTolerateFailure() => ++ConsecutiveFailures <= maxConsecutiveFailures;

    /// <summary>任务执行成功或恢复成功时清零，保证容忍只针对"连续"失败。</summary>
    internal void Reset() => ConsecutiveFailures = 0;
}
