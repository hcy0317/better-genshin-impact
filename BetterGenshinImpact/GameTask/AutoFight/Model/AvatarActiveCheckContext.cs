namespace BetterGenshinImpact.GameTask.AutoFight.Model;

/// <summary>
/// 多次识别出战角色结果上下文
/// </summary>
public class AvatarActiveCheckContext
{
    /// <summary>
    /// 出战标识识别结果的次数统计
    /// </summary>
    public int[] ActiveIndexByArrowCount { get; set; } = new int[4];
    
    /// <summary>
    /// 累计识别失败次数
    /// </summary>
    public int TotalCheckFailedCount { get; set; } = 0;
}

internal static class AvatarSwitchConfirmationPolicy
{
    private const int RequiredConsecutiveTargetFrames = 2;

    internal static bool TryConfirm(int expectedIndex, int attempts, System.Func<int> observe,
        System.Action<int> switchAvatar, System.Action<int> wait, System.Threading.CancellationToken ct,
        System.Action<int, int>? onMismatch = null)
    {
        var consecutive = 0;
        for (var i = 0; i < attempts; i++)
        {
            if (ct.IsCancellationRequested) return false;
            var current = observe();
            if (ct.IsCancellationRequested) return false;
            consecutive = Observe(consecutive, current, expectedIndex);
            if (IsConfirmed(consecutive)) return true;
            if (current != expectedIndex)
            {
                onMismatch?.Invoke(i, current);
                if (ct.IsCancellationRequested) return false;
                switchAvatar(expectedIndex);
                wait(250);
            }
            else wait(100);
        }
        return false;
    }

    internal static int Observe(
        int consecutiveTargetFrames,
        int observedIndex,
        int expectedIndex)
    {
        return observedIndex == expectedIndex
            ? consecutiveTargetFrames + 1
            : 0;
    }

    internal static bool IsConfirmed(int consecutiveTargetFrames)
    {
        return consecutiveTargetFrames >= RequiredConsecutiveTargetFrames;
    }
}
