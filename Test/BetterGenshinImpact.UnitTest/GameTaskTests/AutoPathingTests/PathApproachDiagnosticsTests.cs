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
    public async Task RecoveryTerminalKeepsTheWholeInputChainAndOriginalPosition()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var probe = new PathApproachDiagnostics("pillar", "node=21");
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        probe.Recovery(frame, new(frame.FrameStamp, new(3, 4), true, BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal, true), "before", NullLogger.Instance);
        var first = new DiagnosticInputReceipt(Guid.NewGuid(), 0, 0, 1, clock.TimestampFrequency, 1, 1, DiagnosticInputStatus.Sent, "observed");
        probe.RecordRecoveryInput(BetterGenshinImpact.Core.Simulator.Extensions.GIActions.MoveBackward,
            BetterGenshinImpact.Core.Simulator.Extensions.KeyType.KeyDown, first, "fixture");
        var release = first with { RequestId = Guid.NewGuid(), CompletedAt = 2 };
        probe.RecordRecoveryInput(BetterGenshinImpact.Core.Simulator.Extensions.GIActions.MoveBackward,
            BetterGenshinImpact.Core.Simulator.Extensions.KeyType.KeyUp, release, "fixture");
        clock.Advance(TimeSpan.FromMilliseconds(60));
        frame.FrameStamp = source.Next();
        probe.Recovery(frame, new(frame.FrameStamp, new(6, 8), true, BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal, true), "terminal-returned", NullLogger.Instance);
        await evidence.DisposeAsync();
        var terminal = Assert.Single(saved.Where(item => item.Phase == "ground-recovery-terminal-returned"));
        Assert.Contains(first.RequestId.ToString("N"), terminal.Fields!["recoveryInputs"]);
        Assert.Contains(release.RequestId.ToString("N"), terminal.Fields["recoveryInputs"]);
        Assert.Contains("position=(3,4)", terminal.Fields["recoveryStart"]);
        Assert.Contains("position=(6,8)", terminal.Fields["recoveryEnd"]);
    }

    [Fact]
    public async Task GroundRecoveryRetainsTheInterruptedSourceAndActualInputReceiptOnce()
    {
        var records = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { records.Add(item); return Task.CompletedTask; });
        var probe = new PathApproachDiagnostics("wooden-pillar", "node=21");
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        probe.Recovery(frame, new(frame.FrameStamp, new(3, 4), true, BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Normal, true), "before", NullLogger.Instance);
        using var input = new DiagnosticInputAttempt(clock);
        new WindowsInputMessageDispatcher(null, inputs => (uint)inputs.Length, () => 0).DispatchInput(new User32.INPUT[2]);
        probe.RecordRecoveryInput(BetterGenshinImpact.Core.Simulator.Extensions.GIActions.Jump,
            BetterGenshinImpact.Core.Simulator.Extensions.KeyType.KeyPress, input.Complete(true), "backend=fixture foregroundIsGame=True");
        clock.Advance(TimeSpan.FromMilliseconds(60));
        frame.FrameStamp = source.Next();
        var interrupted = frame.FrameStamp;
        probe.Recovery(frame, new(interrupted, new(3, 4), true, BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Fly, true), "interrupted", NullLogger.Instance);
        clock.Advance(TimeSpan.FromMilliseconds(60));
        frame.FrameStamp = source.Next();
        probe.Recovery(frame, new(frame.FrameStamp, new(3, 4), true, BetterGenshinImpact.GameTask.Common.BgiVision.MotionStatus.Fly, true), "interrupted", NullLogger.Instance);
        await evidence.DisposeAsync();
        Assert.Equal(2, records.Count);
        var original = Assert.Single(records.Where(item => item.Phase == "ground-recovery-interrupted"));
        Assert.Equal(interrupted, original.Source);
        Assert.Contains("action=Jump", original.Fields!["nativeInput"]);
        Assert.Contains("nativeRequested=2 nativeSubmitted=2", original.Fields["nativeInput"]);
        Assert.Contains("foregroundIsGame=True", original.Fields["nativeInputEnvironment"]);
        Assert.False(frame.SrcMat.IsDisposed);
    }

    [Fact]
    public async Task ExhaustedAttemptsKeepDistinctOriginalFramesAndDoNotOwnCallerPixels()
    {
        var records = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { records.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var source = new CaptureFrameSource();
        for (var i = 0; i < 2; i++)
        {
            frame.FrameStamp = source.Next();
            new PathApproachDiagnostics("same-route", "node=14").Exhausted(frame,
                new PathPosition(new(3, 4), 0, true), new(5, 6), "Normal", NullLogger.Instance);
        }
        await scope.DisposeAsync();
        Assert.Equal(2, records.Count);
        Assert.Equal(2, records.Select(x => x.Request).Distinct().Count());
        Assert.All(records, x => Assert.Equal("precise-exhausted", x.Phase));
        Assert.False(frame.SrcMat.IsDisposed);
    }

    [Fact]
    public async Task FailedPulsePinsTheUnsampledInputFrameAndKeepsTheOriginalException()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        frame.FrameStamp = source.Next();
        scope.ObserveExistingFrame(frame);
        clock.Advance(TimeSpan.FromMilliseconds(30));
        frame.FrameStamp = source.Next();
        scope.ObserveExistingFrame(frame);
        var failure = new IOException("fixture");
        var dispatcher = new WindowsInputMessageDispatcher(null, _ => throw failure, () => 0);
        var actual = await Assert.ThrowsAsync<IOException>(() => PathApproachDiagnostics.RunPulseAsync(
            () => dispatcher.DispatchInput(new User32.INPUT[1]), () => { }, _ => Task.CompletedTask, clock, frame));
        Assert.Same(failure, actual);
        await scope.DisposeAsync();
        var anchor = Assert.Single(saved.Where(item => item.Window?.RelativeIndex == 0));
        Assert.Equal(frame.FrameStamp, anchor.Source);
        Assert.Contains("status=Unknown", anchor.Fields!["nativeInput"]);
        Assert.Contains("reason=IOException", anchor.Fields["nativeInput"]);
        Assert.False(frame.SrcMat.IsDisposed);
    }

    [Theory]
    [InlineData(2, "cache")]
    [InlineData(3, "fallback")]
    [InlineData(4, "invalid")]
    public async Task StallEvidencePreservesTheActualLocationSource(int sourceKind, string expected)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var probe = new PathApproachDiagnostics("source", "node=1");
        var source = new CaptureFrameSource();
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 0; i < 6; i++)
        {
            frame.FrameStamp = source.Next();
            probe.Observe(frame, new(10, 10), new(20, 10), 10, i, NullLogger.Instance,
                directPosition: false, locationSource: (PathPositionSource)sourceKind);
        }
        await scope.DisposeAsync();
        Assert.Contains("locationSource=" + expected + " ", Assert.Single(saved.Where(item => item.Window?.RelativeIndex == 0)).Detail);
    }

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
            probe.Observe(frame, new(10, 10), new(12.24f, 10), 2.24, i + 1, NullLogger.Instance, directPosition,
                navigation: new(true, frame.FrameStamp, "SIFT", "TemplateMatch", "requested-floor-1", "actual-layer-2", 2, .87, true, "local", "roi-fixture"));
            Assert.False(frame.SrcMat.IsDisposed);
        }
        await evidence.DisposeAsync();
        var saved = Assert.Single(records.Where(item => item.Window?.RelativeIndex == 0));
        Assert.Equal("precise-stall", saved.Phase);
        Assert.Contains("requested=2 submitted=2", saved.Detail);
        Assert.Contains("actualLayer=actual-layer-2", saved.Detail);
        Assert.Contains("candidateScore=0.87", saved.Detail);
        Assert.Contains("actualMethod=TemplateMatch", saved.Detail);
        Assert.Contains(directPosition == true ? "locationSource=direct" : "locationSource=unknown", saved.Detail);
        if (staleDuplicate) Assert.Contains("sourceAdvanced=False", saved.Detail);
    }
}
