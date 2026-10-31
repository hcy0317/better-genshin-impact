using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathApproachPulseEvidenceTests
{
    [Fact]
    public async Task UnexpectedSwimKeepsTheForwardReceiptAndItsOriginalInputFrame()
    {
        var records = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { records.Add(item); return Task.CompletedTask; });
        var probe = new PathApproachDiagnostics("06", "node=24");
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = producer.Next() };
        var original = frame.FrameStamp;
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0);
        var pulse = await PathApproachDiagnostics.RunPulseAsync(
            () => dispatcher.DispatchInput(new User32.INPUT[1]),
            () => dispatcher.DispatchInput(new User32.INPUT[1]),
            ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); frame.FrameStamp = producer.Next(); return Task.CompletedTask; }, clock, frame);
        probe.RecordPulse(pulse);
        frame.FrameStamp = producer.Next();
        probe.Recovery(frame, new(frame.FrameStamp, new(3, 4), true, MotionStatus.Swim, true), "unexpected-swimming", NullLogger.Instance);
        await scope.DisposeAsync();
        var saved = Assert.Single(records);
        Assert.True(saved.Fields!.TryGetValue("approachInput", out var input), "普通前进回执必须进入游泳失败证据");
        Assert.Contains(pulse.Receipt!.RequestId.ToString("N"), input!);
        Assert.Contains("nativeRequested=2 nativeSubmitted=2", input!);
        Assert.Contains("status=Sent", input!);
        Assert.Equal($"source={original.SessionId}/{original.Sequence} captured={original.CapturedTimestamp} frequency={original.TimestampFrequency}", saved.Fields["approachInputSource"]);
        Assert.Equal("ground-recovery", saved.Fields["nativeInputScope"]);
        Assert.Equal("not-submitted", saved.Fields["nativeInput"]);
        Assert.Equal(frame.FrameStamp, saved.Source);
        Assert.False(frame.SrcMat.IsDisposed);
    }

    [Fact]
    public async Task SidewaysRecoveryDoesNotReplaceTheOrdinaryApproachReceipt()
    {
        var records = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { records.Add(item); return Task.CompletedTask; });
        var probe = new PathApproachDiagnostics("06", "node=24");
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = producer.Next() };
        var forward = new DiagnosticInputReceipt(Guid.NewGuid(), 1, 2, 3, clock.TimestampFrequency, 2, 2, DiagnosticInputStatus.Sent, "observed");
        var sideways = forward with { RequestId = Guid.NewGuid(), RequestedAt = 4, StartedAt = 5, CompletedAt = 6 };
        probe.RecordPulse(new(2, 2, false, 60, forward));
        probe.RecordRecoveryInput(GIActions.MoveLeft, KeyType.KeyDown, sideways, "fixture");
        probe.Recovery(frame, new(frame.FrameStamp, new(3, 4), true, MotionStatus.Swim, true), "unexpected-swimming", NullLogger.Instance);
        await scope.DisposeAsync();
        var saved = Assert.Single(records);
        Assert.True(saved.Fields!.TryGetValue("approachInput", out var input), "侧移不能覆盖普通接近回执");
        Assert.Contains(forward.RequestId.ToString("N"), input!);
        Assert.Contains(sideways.RequestId.ToString("N"), saved.Fields["nativeInput"]);
        Assert.Equal("ground-recovery", saved.Fields["nativeInputScope"]);
        Assert.Contains(sideways.RequestId.ToString("N"), saved.Fields["recoveryInputs"]);
    }

    [Theory]
    [InlineData(2, 1, (int)DiagnosticInputStatus.Unknown)]
    [InlineData(0, 0, (int)DiagnosticInputStatus.NotSent)]
    public async Task ApproachEvidenceRetainsPartialAndNotSentOutcomes(int requested, int submitted, int statusValue)
    {
        var status = (DiagnosticInputStatus)statusValue;
        var records = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { records.Add(item); return Task.CompletedTask; });
        var probe = new PathApproachDiagnostics("06", "node=24");
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
        var receipt = new DiagnosticInputReceipt(Guid.NewGuid(), 1, requested > 0 ? 2 : null, 3, TimeProvider.System.TimestampFrequency, requested, submitted, status, "fixture");
        probe.RecordPulse(new(requested, submitted, status == DiagnosticInputStatus.Unknown, 60, receipt));
        probe.Recovery(frame, new(frame.FrameStamp, new(3, 4), true, MotionStatus.Swim, true), "unexpected-swimming", NullLogger.Instance);
        await scope.DisposeAsync();
        var saved = Assert.Single(records);
        Assert.True(saved.Fields!.TryGetValue("approachInput", out var input), "不确定或未发送回执不能丢失或改写成功");
        Assert.Equal(receipt.Describe(), input);
        Assert.Equal("unknown:not-supplied", saved.Fields["approachInputSource"]);
    }

    [Fact]
    public async Task MissingApproachReceiptStaysUnknown()
    {
        var records = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { records.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = new CaptureFrameSource().Next() };
        new PathApproachDiagnostics("06", "node=24").Recovery(frame,
            new(frame.FrameStamp, new(3, 4), true, MotionStatus.Swim, true), "unexpected-swimming", NullLogger.Instance);
        await scope.DisposeAsync();
        var saved = Assert.Single(records);
        Assert.True(saved.Fields!.TryGetValue("approachInput", out var input), "未观察到普通脉冲时须明确unknown");
        Assert.Equal("unknown:no-approach-pulse-observed", input);
        Assert.Equal("unknown:not-supplied", saved.Fields["approachInputSource"]);
    }
}
