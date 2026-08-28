using System;
using System.Threading.Tasks;

namespace BetterGenshinImpact.GameTask.ArtifactAnalysis;

internal static class ArtifactHostGameContext
{
    internal static async Task EnsureReadyAsync(
        Func<Task> startGameTask,
        Func<bool> isReady)
    {
        if (isReady()) return;
        await startGameTask();
        if (!isReady())
        {
            throw new InvalidOperationException(
                "BetterGI 截图器或游戏上下文尚未初始化，无法检测角色列表。");
        }
    }
}
