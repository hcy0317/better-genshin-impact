using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service.ChildSession;
using BetterGenshinImpact.Service.Interface;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

public class ChildSessionAutomationSettingsTests
{
    [Fact]
    public void ReusedConfigurationResetsOnlyAutomationSettingsAndFlushesThroughItsOwner()
    {
        var config = new AllConfig { CaptureMode = "WindowsGraphicsCaptureHdr" };
        config.AutoPickConfig.Enabled = false;
        config.AutoPickConfig.PickKey = "YY";
        config.AutoPickConfig.ItemTextLeftOffset = 123;
        config.MaskWindowConfig.ShowLogBox = config.MaskWindowConfig.ShowStatus = false;
        config.HardwareAccelerationConfig.CpuOcr = false;
        config.HardwareAccelerationConfig.GpuDevice = 2;
        config.ChildSessionConfig.AudioMuted = false;
        var pick = config.AutoPickConfig;
        var service = new FakeConfigService(config);

        ChildSessionAutomationSettings.Apply(service);

        Assert.Equal("BitBlt", config.CaptureMode);
        Assert.True(config.AutoPickConfig.Enabled);
        Assert.Equal("F", config.AutoPickConfig.PickKey);
        Assert.True(config.MaskWindowConfig.ShowLogBox);
        Assert.True(config.MaskWindowConfig.ShowStatus);
        Assert.True(config.HardwareAccelerationConfig.CpuOcr);
        Assert.Same(pick, config.AutoPickConfig);
        Assert.Equal(123, config.AutoPickConfig.ItemTextLeftOffset);
        Assert.Equal(2, config.HardwareAccelerationConfig.GpuDevice);
        Assert.False(config.ChildSessionConfig.AudioMuted);
        Assert.Equal(1, service.Flushes);
        ChildSessionAutomationSettings.Apply(service);
        Assert.Equal(2, service.Flushes);
    }

    private sealed class FakeConfigService(AllConfig config) : IConfigService
    {
        internal int Flushes;
        public AllConfig Get() => config;
        public void Flush() => Flushes++;
        public AllConfig Read() => throw new InvalidOperationException("Do not replace a live cached configuration");
        public void Save() => throw new InvalidOperationException("Use the owner's pending-change flush");
        public void Write(AllConfig value) => throw new InvalidOperationException("Do not overwrite unrelated configuration");
    }
}
