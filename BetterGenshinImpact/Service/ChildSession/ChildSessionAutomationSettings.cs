using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.Interface;
using Fischless.GameCapture;

namespace BetterGenshinImpact.Service.ChildSession;

/// <summary>无人值守分身启动时复位操作设置，不修改一条龙内容或用户的拾取名单。</summary>
internal static class ChildSessionAutomationSettings
{
    internal static void Apply(IConfigService service)
    {
        var config = service.Get();
        config.CaptureMode = nameof(CaptureModes.BitBlt);
        config.AutoPickConfig.Enabled = true;
        config.AutoPickConfig.PickKey = "F";
        config.MaskWindowConfig.ShowLogBox = true;
        config.MaskWindowConfig.ShowStatus = true;
        config.HardwareAccelerationConfig.CpuOcr = true;
        service.Flush();
    }
}
