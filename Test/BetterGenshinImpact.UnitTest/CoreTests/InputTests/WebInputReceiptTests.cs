using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Input.Backends.WebSdk;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight;
using Fischless.WindowsInput;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.CoreTests.InputTests;

public class WebInputReceiptTests
{
    [Fact]
    public void AcknowledgedSdkCallDoesNotFabricateNativeCounts()
    {
        var bridge = new FakeBridge();
        var channel = new WebSdkInputBackend(bridge, () => new RECT(0, 0, 1920, 1080));
        var admissions = 0;
        using var capture = new InputDispatchCapture(() => admissions++);
        channel.SimulateKeyPulse(KeyId.F);
        Assert.Equal(1, admissions);
        Assert.Equal(new[] { "keyDown", "keyUp" }, bridge.Methods);
        Assert.Equal(0, capture.NativeCalls);
        Assert.Equal(0, capture.Submitted);
        Assert.Equal(2, capture.TransportAcknowledged);
        Assert.True(capture.HasCompleteReceipt);
        Assert.Equal(CombatBattleHostInputStatus.Sent, CombatNativeInput.Classify(capture, null, 123).Status);
    }

    [Fact]
    public void RejectedAdmissionDoesNotPostOrTryCleanup()
    {
        var bridge = new FakeBridge();
        var channel = new WebSdkInputBackend(bridge, () => default);
        using var capture = new InputDispatchCapture(() => throw new TimeoutException());
        Assert.Throws<TimeoutException>(() => channel.SimulateKeyPulse(KeyId.F));
        Assert.Empty(bridge.Methods);
        Assert.False(capture.HasDispatch);
        Assert.False(capture.Uncertain);
    }

    [Fact]
    public void LostDownAcknowledgementAttemptsUpAndRemainsUnknown()
    {
        var bridge = new FakeBridge { FailDown = true };
        var channel = new WebSdkInputBackend(bridge, () => default);
        using var capture = new InputDispatchCapture();
        var error = Assert.Throws<TimeoutException>(() => channel.SimulateKeyPulse(KeyId.F));
        Assert.Equal(new[] { "keyDown", "keyUp" }, bridge.Methods);
        Assert.True(capture.Uncertain);
        Assert.False(capture.HasCompleteReceipt);
        Assert.Equal(0, capture.Submitted);
        Assert.Equal(CombatBattleHostInputStatus.Unknown, CombatNativeInput.Classify(capture, error, 123).Status);
    }

    [Fact]
    public void NestedCapturesObserveTransportWithoutRepeatingAdmission()
    {
        var admissions = 0;
        using var outer = new InputDispatchCapture(() => admissions++);
        using (var inner = new InputDispatchCapture())
        {
            InputDispatchCapture.DispatchTransport(admit => admit());
            InputDispatchCapture.DispatchTransport(admit => admit());
            Assert.Equal(2, inner.TransportAcknowledged);
        }
        Assert.Equal(2, outer.TransportAcknowledged);
        Assert.Equal(1, admissions);
        Assert.True(outer.HasCompleteReceipt);
    }

    private sealed class FakeBridge : IWebInputBridge
    {
        public readonly List<string> Methods = [];
        public bool FailDown;
        public void Invoke(string method, params object[] args) => throw new InvalidOperationException("unconfirmed path used");
        public void InvokeAcknowledged(string method, Action beforePost, params object[] args)
        {
            beforePost();
            Methods.Add(method);
            if (FailDown && method == "keyDown") throw new TimeoutException("fake lost acknowledgement");
        }
    }
}
