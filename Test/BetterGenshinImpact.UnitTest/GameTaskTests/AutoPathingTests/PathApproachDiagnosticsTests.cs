using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathApproachDiagnosticsTests
{
    [Fact]
    public void FailedPulsePreservesNativeAndCleanupErrorsWithoutRepeatingDown()
    {
        var original = new IOException("down failed");
        var cleanup = new IOException("up failed");
        var stage = "down";
        var calls = new List<string>();
        var dispatcher = new WindowsInputMessageDispatcher(null, _ =>
        { calls.Add(stage); throw stage == "down" ? original : cleanup; }, () => 0);
        var failure = Assert.Throws<AggregateException>(() => PathApproachDiagnostics.RunPulse(
            () => dispatcher.DispatchInput(new User32.INPUT[1]),
            () => { stage = "up"; dispatcher.DispatchInput(new User32.INPUT[1]); }, _ => { }));
        Assert.Equal(new Exception[] { original, cleanup }, failure.InnerExceptions);
        Assert.Equal(new[] { "down", "up" }, calls);
    }

    [Fact]
    public async Task ActualProgressDoesNotCreateStallEvidence()
    {
        var records = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { records.Add(item); return Task.CompletedTask; });
        var probe = new PathApproachDiagnostics("moving", "node=1");
        var producer = new CaptureFrameSource();
        for (var i = 0; i < 10; i++)
        {
            using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = producer.Next() };
            probe.Observe(frame, new(i, 0), new(20, 0), 20 - i, i + 1, NullLogger.Instance);
        }
        await evidence.DisposeAsync();
        Assert.Empty(records);
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task StationaryApproachCapturesOneBorrowedFrameWithRealPulseCounts(bool staleDuplicate, bool? directPosition)
    {
        var records = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { records.Add(item); return Task.CompletedTask; });
        var probe = new PathApproachDiagnostics("305", "node=8 action=none");
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0);
        var clock = new FakeTimeProvider();
        clock.Advance(TimeSpan.FromSeconds(3));
        var producer = new CaptureFrameSource(staleDuplicate ? clock : null);
        var duplicate = producer.Next(0);
        for (var i = 0; i < 10; i++)
        {
            probe.RecordPulse(PathApproachDiagnostics.RunPulse(
                () => dispatcher.DispatchInput(new User32.INPUT[1]),
                () => dispatcher.DispatchInput(new User32.INPUT[1]), _ => { }));
            using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
            { FrameStamp = staleDuplicate ? duplicate : producer.Next() };
            probe.Observe(frame, new(10, 10), new(12.24f, 10), 2.24, i + 1, NullLogger.Instance, directPosition);
            Assert.False(frame.SrcMat.IsDisposed);
        }
        await evidence.DisposeAsync();
        var saved = Assert.Single(records);
        Assert.Equal("precise-stall", saved.Phase);
        Assert.Contains("requested=2 submitted=2", saved.Detail);
        Assert.Contains(directPosition == true ? "locationSource=direct" : directPosition == false
            ? "locationSource=fallback-or-invalid" : "locationSource=unknown", saved.Detail);
        if (staleDuplicate) Assert.Contains("sourceAdvanced=False", saved.Detail);
    }
}
