using System;

namespace BetterGenshinImpact.GameTask.Common;

internal static class GameProcessExitGuard
{
    internal static void ThrowIfExited(bool contextInitialized, Func<bool> hasExited)
    {
        if (contextInitialized && hasExited())
        {
            throw new InvalidOperationException("检测到原神进程已退出，停止窗口焦点恢复。");
        }
    }
}
