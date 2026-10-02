using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.Core.Config;
using Fischless.WindowsInput;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatInputPulseTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeAdmissionFailureCannotTurnCleanupIntoANewInput(bool channel)
    {
        var submissions = 0;
        var admissions = 0;
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs => { submissions++; return (uint)inputs.Length; }, () => 0);
        var keyboard = new KeyboardSimulator(new InputSimulator(), dispatcher);
        var simulator = new InputSimulator(keyboard, null!, null!);
        using var capture = new InputDispatchCapture(() => { admissions++; throw new TimeoutException("expired"); });
        Assert.Throws<TimeoutException>(() =>
        {
            if (channel) new BetterGenshinImpact.Core.Input.Backends.Win32.SendInputChannel(simulator).SimulateKeyPulse(KeyId.F);
            else simulator.SimulateKeyPulse(KeyId.F);
        });
        Assert.Equal(0, submissions);
        Assert.Equal(1, admissions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterNativeDownStillSubmitsThePairedUp(bool channel)
    {
        using var cancellation = new CancellationTokenSource();
        var submissions = 0;
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs =>
        {
            submissions++;
            cancellation.Cancel();
            return (uint)inputs.Length;
        }, () => 0);
        var keyboard = new KeyboardSimulator(new InputSimulator(), dispatcher);
        var simulator = new InputSimulator(keyboard, null!, null!);
        using var capture = new InputDispatchCapture();
        Assert.Throws<OperationCanceledException>(() =>
        {
            if (channel) new BetterGenshinImpact.Core.Input.Backends.Win32.SendInputChannel(simulator).SimulateKeyPulse(KeyId.F, cancellation.Token);
            else simulator.SimulateKeyPulse(KeyId.F, cancellation.Token);
        });
        Assert.Equal(2, submissions);
        Assert.Equal(2, capture.Submitted);
    }

    [Fact]
    public void LegacyKeyPressHasNoObservableHoldAcrossAThirtyFpsFrame()
    {
        var batches = new List<User32.INPUT[]>();
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs =>
        {
            batches.Add(inputs);
            return (uint)inputs.Length;
        }, () => 0);
        var keyboard = new KeyboardSimulator(new InputSimulator(), dispatcher);
        keyboard.KeyPress(User32.VK.VK_E);
        Assert.Single(batches);
        Assert.Equal(2, batches[0].Length);
    }

    [Fact]
    public void PulseKeepsKeyDownAcrossEveryThirtyFpsSamplingPhase()
    {
        var held = false;
        var elapsed = 0;
        var events = new List<string>();
        InputSimulatorExtension.PulseCore(() => { held = true; events.Add("down"); },
            () => { held = false; events.Add("up"); }, milliseconds =>
            {
                Assert.True(held);
                elapsed += milliseconds;
                events.Add("wait");
            }, () => { });
        Assert.Equal(new[] {"down", "wait", "up"}, events);
        Assert.InRange(elapsed, 60, 100);
        for (var phase = 0; phase < 34; phase++) Assert.True(phase < elapsed);
        Assert.False(held);
    }

    [Fact]
    public void CancelBeforeDownSendsNothing()
    {
        var calls = 0;
        Assert.Throws<OperationCanceledException>(() => InputSimulatorExtension.PulseCore(
            () => calls++, () => calls++, _ => calls++, () => throw new OperationCanceledException()));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FailedDownOrInterruptedHoldStillReleases(bool failDown)
    {
        var releases = 0;
        Assert.Throws<OperationCanceledException>(() => InputSimulatorExtension.PulseCore(
            () => { if (failDown) throw new OperationCanceledException(); },
            () => releases++, _ => throw new OperationCanceledException(), () => { }));
        Assert.Equal(1, releases);
    }
}
