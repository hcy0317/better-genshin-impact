using Fischless.GameCapture.Graphics;
using SharpDX.Direct3D11;

namespace BetterGenshinImpact.UnitTest.CoreTests.CaptureTests;

public class GraphicsCaptureV2Tests
{
    [Fact]
    public void OwnedDeviceProtectsItsImmediateContextAcrossProducerAndConsumerThreads()
    {
        // 仅创建离屏D3D设备，不启动WGC、不捕获窗口或操作桌面。
        using var device = GraphicsCaptureV2.CreateCaptureDevice();
        using var protection = device.ImmediateContext.QueryInterface<Multithread>();
        Assert.True(protection.GetMultithreadProtected());
    }
}
