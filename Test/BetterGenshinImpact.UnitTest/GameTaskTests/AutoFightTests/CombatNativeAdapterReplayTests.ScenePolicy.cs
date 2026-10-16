using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeTargetLockRequiresDeclaredScenePermissionAtTheSendBoundary(bool allowed)
    {
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), physical);
        var device = new HostDevice(clock);
        var native = new NativeCombatBattleHostIo(flow, device);
        var input = new CombatBattleHostInput(CombatBattleHostInputKind.TargetLock)
        {
            RequestId = Guid.NewGuid(), Source = new CaptureFrameSource(clock).Next(),
            DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency,
            Scene = new(new(.34, .08, .66, .12), true, allowed)
        };
        try
        {
            var result = await native.SendAsync(input, default);
            Assert.Equal(allowed ? CombatBattleHostInputStatus.Sent : CombatBattleHostInputStatus.Failed, result.Status);
            if (allowed) Assert.Equal("target-lock", Assert.Single(device.Inputs));
            else Assert.Empty(device.Inputs);
        }
        finally { native.ReleaseInput(); }
    }
}
