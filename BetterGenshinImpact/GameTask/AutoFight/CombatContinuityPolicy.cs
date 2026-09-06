using System;

namespace BetterGenshinImpact.GameTask.AutoFight;

internal static class CombatContinuityPolicy
{
    // 主执行线程只消费已发布的观察，不截图、不等待视觉收敛。
    internal static int CameraPulse(int targetX, int imageWidth)
    {
        if (targetX <= 0 || imageWidth <= 0 || targetX >= imageWidth) return 0;
        var offset = targetX - imageWidth / 2;
        return Math.Abs(offset) <= imageWidth / 10 ? 0 : Math.Clamp(offset / 3, -120, 120);
    }
}
