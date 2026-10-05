using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;
using Fischless.GameCapture;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathWorldEvidenceTests
{
    [Fact]
    public async Task RecoveryKeepsTwoFreshChecksAndBorrowsTheirExactFramesWithoutExtraInput()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var frames = new List<ImageRegion>();
        var inputs = new List<KeyType>();
        var kinds = new[] { WorldFrameKind.Unknown, WorldFrameKind.Playable, WorldFrameKind.Playable, WorldFrameKind.Playable };
        var reads = 0;
        var orientations = 0;
        var io = new PathMoveToIo
        {
            Clock = clock,
            Capture = () => { var frame = Frame(source); frames.Add(frame); return frame; },
            Availability = _ => kinds[reads++],
            CameraOrientation = _ => ++orientations == 1 ? float.NaN : 42,
            Send = (_, kind) => inputs.Add(kind),
            Delay = (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }
        };
        using var result = await PathWorldAvailability.CapturePlayableAsync(io, clock.GetUtcNow().AddSeconds(2), default);
        Assert.Equal(4, reads);
        Assert.Equal(3, orientations);
        Assert.Equal(5, inputs.Count);
        Assert.All(inputs, kind => Assert.Equal(KeyType.KeyUp, kind));
        Assert.All(frames.Take(3), frame => Assert.True(frame.SrcMat.IsDisposed));
        Assert.False(result.SrcMat.IsDisposed);
        await evidence.DisposeAsync();
        var recovered = Assert.Single(saved.Where(item => item.Phase == "path-world-recovered"));
        Assert.Equal(result.FrameStamp, recovered.Source);
        Assert.Contains("world=Playable", recovered.Fields!["world:last"]);
        Assert.Contains("orientation=42", recovered.Fields["world:last"]);
        Assert.Contains("blockedFrames=2", recovered.Fields["world:last"]);
        Assert.Contains($"{frames[0].FrameStamp.SessionId}/{frames[0].FrameStamp.Sequence}", recovered.Fields["world:firstSource"]);
    }

    [Fact]
    public async Task RecognitionConsumesOriginalDeadlineAndFailureUsesItsDecisionFrame()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var evidence = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var frame = Frame(source);
        var captures = 0;
        var io = new PathMoveToIo
        {
            Clock = clock,
            Capture = () => { captures++; return frame; },
            Availability = _ => { clock.Advance(TimeSpan.FromSeconds(3)); return WorldFrameKind.Unknown; },
            Send = (_, _) => throw new Exception("deadline must be checked before releasing input"),
            Delay = (_, _) => throw new Exception("deadline must not be renewed")
        };
        await Assert.ThrowsAsync<RetryException>(() => PathWorldAvailability.CapturePlayableAsync(io, clock.GetUtcNow().AddSeconds(2), default));
        Assert.Equal(1, captures);
        Assert.True(frame.SrcMat.IsDisposed);
        await evidence.DisposeAsync();
        var failed = Assert.Single(saved.Where(item => item.Phase == "path-world-deadline"));
        Assert.Equal(frame.FrameStamp, failed.Source);
        Assert.Contains("fresh=False", failed.Fields!["world:last"]);
        Assert.Contains("RetryException", failed.Fields["world:failure"]);
    }

    [Fact]
    public async Task FailingEvidenceWriterAndLoggerFactoryPreserveOriginalRecognitionException()
    {
        await using var evidence = new DiagnosticEvidenceScope((_, _) => throw new IOException("evidence disk"));
        var clock = new FakeTimeProvider();
        var frame = Frame(new CaptureFrameSource(clock));
        var original = new IOException("recognition");
        var io = new PathMoveToIo
        {
            Clock = clock,
            LoggerFactory = () => throw new IOException("logger"),
            Capture = () => frame,
            Availability = _ => throw original
        };
        Assert.Same(original, await Assert.ThrowsAsync<IOException>(() =>
            PathWorldAvailability.CapturePlayableAsync(io, clock.GetUtcNow().AddSeconds(2), default)));
        Assert.True(frame.SrcMat.IsDisposed);
        await evidence.DisposeAsync();
    }

    private static ImageRegion Frame(CaptureFrameSource source) => new(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
    { FrameStamp = source.Next() };
}
