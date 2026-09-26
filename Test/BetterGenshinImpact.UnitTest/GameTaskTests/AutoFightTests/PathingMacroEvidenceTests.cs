using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class PathingMacroEvidenceTests
{
    [Fact]
    public async Task UnknownBoundaryKeepsOneFrameThenTheRecognizedFrameAndIgnoresUnboundedPhases()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var evidence = new PathingMacroEvidence("node=5", "keypress(t)");
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        for (var i = 0; i < 20; i++)
        {
            evidence.Capture(frame, "entry", new(PathingMacroScene.Unknown, frame.FrameStamp));
            evidence.Capture(frame, "tick-" + i, new(PathingMacroScene.World, frame.FrameStamp));
        }
        evidence.Capture(frame, "entry", new(PathingMacroScene.World, frame.FrameStamp));
        await scope.DisposeAsync();
        Assert.Equal(new[] { "entry-unknown", "entry" }, saved.Select(x => x.Phase));
    }

    [Fact]
    public async Task FailingSinkCannotEscapeIntoMacroExecution()
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => throw new IOException("disk unavailable"));
        var evidence = new PathingMacroEvidence("node=5", "keypress(t)");
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        evidence.Capture(frame, "entry", new(PathingMacroScene.World, frame.FrameStamp));
        evidence.Capture(frame, "post", new(PathingMacroScene.World, frame.FrameStamp));
        await scope.DisposeAsync();
        Assert.False(frame.SrcMat.IsDisposed);
    }

    [Fact]
    public async Task SuccessfulMacroKeepsEntryAndPostWithoutMixingRepeatedExecutions()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var evidence = new PathingMacroEvidence("route=target node=5", "keypress(t),wait(0.5),keypress(t)");
            foreach (var phase in new[] { "entry", "entry", "post", "post" })
            {
                using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
                { FrameStamp = source.Next() };
                evidence.Capture(frame, phase, new(PathingMacroScene.World, frame.FrameStamp));
                Assert.False(frame.SrcMat.IsDisposed);
            }
        }
        await scope.DisposeAsync();
        Assert.Equal(4, saved.Count);
        Assert.Equal(2, saved.Select(x => x.Request).Distinct().Count());
        foreach (var group in saved.GroupBy(x => x.Request))
        {
            Assert.Equal(new[] { "entry", "post" }, group.Select(x => x.Phase));
            Assert.All(group, x => { Assert.Contains("node=5", x.Detail); Assert.Contains("keypress(t)", x.Detail); });
            Assert.True(group.Last().Source.IsAfter(group.First().Source));
        }
    }
}
