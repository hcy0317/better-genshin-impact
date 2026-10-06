using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.ViewModel.Pages;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Common;

internal static class NativeInputEnvironment
{
    // 只读标量，不记录窗口标题、进程命令行或截图；调用者在实际提交边界借用。
    internal static string Read(User32.VK? logical = null)
    {
        var foreground = User32.GetForegroundWindow();
        var target = TaskContext.Instance().GameHandle;
        var physical = logical is { } key ? KeyBindingsSettingsPageViewModel.MappingKey(key).ToString() : "not-keyed";
        return $"backend={InputHub.Backend.Kind} logical={logical?.ToString() ?? "not-keyed"} configuredPhysical={physical} foregroundIsGame={foreground == target} targetPresent={target != System.IntPtr.Zero}";
    }
}
