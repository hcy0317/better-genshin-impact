using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatNativeAdapterReplayTests
{
    [Theory]
    [InlineData("off")]
    [InlineData("normal")]
    [InlineData("full")]
    [InlineData("slow")]
    [InlineData("failed")]
    [InlineData("different-frame")]
    public async Task NativeDiagnosticReservationsNeverChooseBusinessFramesOrDelayInput(string mode)
    {
        var clock = new FakeTimeProvider();
        using var physical = new PhysicalReplay(clock, false, 50, LoadProgram("琴 e"));
        using var flow = NativeCombatFlowRunner.Create(LoadProgram("琴 e"), physical);
        var saved = new List<(CaptureFrameStamp Source, byte Pixel)>();
        var writer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scope = mode == "off" ? null : new DiagnosticEvidenceScope(async (item, image) =>
        {
            if (mode == "slow") await writer.Task;
            if (mode == "failed") throw new IOException("test-writer-failure");
            saved.Add((item.Source, image.At<Vec3b>(0, 0).Item0));
        });
        var source = new CaptureFrameSource(clock);
        var latest = source.Next();
        var device = new HostDevice(clock);
        var native = new NativeCombatBattleHostIo(flow, device, targetObservation: () =>
            new(latest, flow.Context.BattleId, CombatObservationQuality.Available, null, 2, 2));
        var input = new CombatBattleHostInput(CombatBattleHostInputKind.Camera, 60)
        { RequestId = Guid.NewGuid(), Source = latest, DeadlineTimestamp = clock.GetTimestamp() + clock.TimestampFrequency };
        var blockers = new List<DiagnosticFrameReservation>();
        try
        {
            if (mode == "full")
                for (var i = 0; i < 2; i++) blockers.Add(scope!.TryReserveExactFrame(
                    new(flow.Context.BattleId, Guid.NewGuid(), AvatarRecognition.PassiveCaptureGate.Epoch), latest,
                    clock, input.DeadlineTimestamp, default)!);
            _ = native.PrepareInputEvidence(input, default);
            clock.Advance(TimeSpan.FromMilliseconds(50));
            latest = source.Next();
            using var pixels = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(27, 0, 0)), 0, 0) { FrameStamp = latest };
            CombatDecisionEvidence.Publish(scope, pixels, new(false, false, clock.GetUtcNow().UtcDateTime, null, 2, 2)
            { Source = latest, BattleId = flow.Context.BattleId, CaptureEpoch = AvatarRecognition.PassiveCaptureGate.Epoch,
                Quality = CombatObservationQuality.Available });
            if (mode == "different-frame") latest = source.Next();
            Assert.Equal(latest, native.ObserveTarget().Source);
            input = input with { Source = latest };
            _ = native.PrepareInputEvidence(input, default);
            var before = clock.GetTimestamp();
            var result = await native.SendAsync(input, default);
            Assert.Equal(CombatBattleHostInputStatus.Sent, result.Status);
            Assert.Equal(before, result.CompletedTimestamp);
            Assert.Equal("camera", Assert.Single(device.Inputs));
            native.EndInputEvidence("test-terminal");
            writer.TrySetResult();
        }
        finally
        {
            writer.TrySetResult();
            native.ReleaseInput();
            foreach (var blocker in blockers) blocker.Dispose();
        }
        if (scope != null)
        {
            await scope.DisposeAsync();
            Assert.Equal(0, scope.ExactReservationStatistics.Active);
        }
        if (mode is "normal" or "slow") Assert.Equal((input.Source, (byte)27), Assert.Single(saved));
        else Assert.Empty(saved);
    }
}
