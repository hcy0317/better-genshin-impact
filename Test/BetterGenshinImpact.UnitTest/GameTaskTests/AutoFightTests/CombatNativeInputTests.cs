using BetterGenshinImpact.GameTask.AutoFight;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatNativeInputTests
{
    [Fact]
    public void CompleteNativeSubmissionRemainsSentWhenSubsequentManagedWorkFails()
    {
        var clock = new FakeTimeProvider();
        var failure = new IOException("after the complete native submission");
        var request = new CombatNativeInputRequest(Guid.NewGuid(), new CaptureFrameSource(clock).Next(),
            clock.GetTimestamp() + clock.TimestampFrequency * 3);
        var native = new WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0);
        var result = new CombatNativeInput(clock, NullLogger.Instance, () => { }).Submit(request, "test", () =>
        {
            native.DispatchInput(new User32.INPUT[2]);
            throw failure;
        }, default);
        Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
        Assert.Equal(2, result.NativeSubmitted);
        Assert.Same(failure, result.Error);
    }

    [Theory]
    [InlineData(0, CombatBattleHostInputStatus.Failed)]
    [InlineData(1, CombatBattleHostInputStatus.Unknown)]
    [InlineData(2, CombatBattleHostInputStatus.Sent)]
    internal void NativeCountsAreNotInferredFromTheActionReturning(uint sent, CombatBattleHostInputStatus expected)
    {
        var clock = new FakeTimeProvider();
        var request = new CombatNativeInputRequest(Guid.NewGuid(), new CaptureFrameSource(clock).Next(), clock.GetTimestamp() + clock.TimestampFrequency * 3);
        var native = new WindowsInputMessageDispatcher(null, _ => sent, () => 5);
        var input = new CombatNativeInput(clock, NullLogger.Instance, () => { });
        var result = input.Submit(request, "test", () => native.DispatchInput(new User32.INPUT[2]), default);
        Assert.Equal(expected, result.Status);
        Assert.Equal((int)sent, result.NativeSubmitted);
    }

    [Theory]
    [InlineData(100, true)]
    [InlineData(200, false)]
    [InlineData(300, false)]
    [InlineData(1800, false)]
    public void WindowPreparationIsCheckedAtTheActualNativeBoundary(int delayMilliseconds, bool admitted)
    {
        var clock = new FakeTimeProvider();
        var request = new CombatNativeInputRequest(Guid.NewGuid(), new CaptureFrameSource(clock).Next(), clock.GetTimestamp() + clock.TimestampFrequency * 3);
        var calls = 0;
        var begins = 0;
        var native = new WindowsInputMessageDispatcher(() => clock.Advance(TimeSpan.FromMilliseconds(delayMilliseconds)),
            inputs => { calls++; return (uint)inputs.Length; }, () => 0);
        var input = new CombatNativeInput(clock, NullLogger.Instance, () => { });
        var result = input.Submit(request, "test", () => native.DispatchInput(new User32.INPUT[2]), default, () => begins++);
        Assert.Equal(admitted ? CombatBattleHostInputStatus.Sent : CombatBattleHostInputStatus.NotSent, result.Status);
        Assert.Equal(admitted ? 1 : 0, calls);
        Assert.Equal(admitted ? 1 : 0, begins);
    }

    [Fact]
    public void ExplicitPhysicalDurationIsNotMisreportedAsLateAdmission()
    {
        var clock = new FakeTimeProvider();
        var before = clock.GetTimestamp();
        var request = new CombatNativeInputRequest(Guid.NewGuid(), new CaptureFrameSource(clock).Next(), clock.GetTimestamp() + clock.TimestampFrequency * 3);
        var native = new WindowsInputMessageDispatcher(null, inputs => { clock.Advance(TimeSpan.FromSeconds(1)); return (uint)inputs.Length; }, () => 0);
        var input = new CombatNativeInput(clock, NullLogger.Instance, () => { });
        var result = input.Submit(request, "hold", () => native.DispatchInput(new User32.INPUT[2]), default);
        Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
        Assert.Equal(before, result.StartedTimestamp);
        Assert.Equal(before + clock.TimestampFrequency, result.CompletedTimestamp);
    }
}
