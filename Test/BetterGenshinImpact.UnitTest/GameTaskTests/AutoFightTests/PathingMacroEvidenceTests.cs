using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class PathingMacroEvidenceTests
{
    [Fact]
    public async Task RepeatedCannonBoundariesKeepEachInputsFirstUnknownAndKnownFrame()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var evidence = new PathingMacroEvidence("node=7", "keypress(f),keypress(f)");
        var source = new CaptureFrameSource();
        for (var input = 0; input < 2; input++)
        {
            evidence.Input(new(PathingMacroInputKind.KeyUp, Vanara.PInvoke.User32.VK.VK_F),
                new(BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputStatus.Sent));
            foreach (var scene in new[] { PathingMacroScene.Unknown, PathingMacroScene.Unknown, PathingMacroScene.Cannon })
            {
                using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
                evidence.Capture(frame, "cannon-handshake-complete", new(scene, frame.FrameStamp));
            }
        }
        await scope.DisposeAsync();
        Assert.Equal(4, saved.Count);
        Assert.Equal(2, saved.Select(x => x.Request).Distinct().Count());
    }

    [Fact]
    public void MissingImageScopeStillLogsBoundedFailureAndInputScalars()
    {
        Assert.Null(DiagnosticEvidenceScope.Current);
        var logger = new EvidenceLogger();
        var evidence = new PathingMacroEvidence("node=7", "keypress(w)");
        evidence.Mapping("VK_W", "VK_A");
        evidence.Input(new(PathingMacroInputKind.KeyDown, Vanara.PInvoke.User32.VK.VK_A),
            new(BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputStatus.Unknown) { NativeRequested = 1, NativeSubmitted = 0 });
        evidence.Failed(new IOException(new string('x', 5000)), logger);
        var message = Assert.Single(logger.Messages);
        Assert.Contains("request=pathing-macro:", message);
        var fields = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(
            message[(message.IndexOf("evidence=", StringComparison.Ordinal) + "evidence=".Length)..])!;
        Assert.Contains("physical=VK_A", fields["input:first"]);
        Assert.Contains("status=Unknown", fields["input:first"]);
        Assert.Equal("VK_W->VK_A", fields["macro:mapping"]);
        Assert.Equal(512, fields["macro:failure"].Length);
    }

    private sealed class EvidenceLogger : Microsoft.Extensions.Logging.ILogger
    {
        internal List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? error, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, error));
    }

    [Fact]
    public async Task FailureKeepsOriginalAndBoundedTailReceiptsWithLastObservationAndMapping()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var evidence = new PathingMacroEvidence("node=7", "keypress(w),keypress(ESCAPE)");
        evidence.Mapping("VK_W", "VK_A");
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 1; i <= 12; i++)
        {
            evidence.Input(new(PathingMacroInputKind.KeyUp, Vanara.PInvoke.User32.VK.VK_A),
                new(BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostInputStatus.Sent, clock.GetTimestamp())
                { NativeRequested = 1, NativeSubmitted = 1 });
            clock.Advance(TimeSpan.FromSeconds(1));
            frame.FrameStamp = source.Next();
            evidence.Capture(frame, "poll", new(PathingMacroScene.Unknown, frame.FrameStamp));
        }
        evidence.Failed(new TimeoutException("original deadline"));
        await scope.DisposeAsync();
        var terminal = Assert.Single(saved.Where(item => item.Phase == "macro-failed" && item.Window?.RelativeIndex == 0));
        var fields = terminal.Fields!;
        Assert.Contains("ordinal=1 ", fields["input:first"]);
        Assert.Contains("ordinal=7 ", fields["input:tail:0"]);
        Assert.Contains("ordinal=12 ", fields["input:tail:5"]);
        Assert.Equal(6, fields.Keys.Count(key => key.StartsWith("input:tail:")));
        Assert.Contains("VK_W->VK_A", fields["macro:mapping"]);
        Assert.Contains("phase=poll", fields["macro:state"]);
        Assert.Contains("inputCount=12", fields["macro:state"]);
        Assert.Contains($"{frame.FrameStamp.SessionId}/{frame.FrameStamp.Sequence}", fields["macro:lastSource"]);
        Assert.Contains("not a new capture", terminal.Detail);
        Assert.All(fields, pair => { Assert.InRange(pair.Key.Length, 1, 64); Assert.InRange(pair.Value.Length, 0, 512); });
        Assert.InRange(fields.Count, 1, 16);
        Assert.False(frame.SrcMat.IsDisposed);
    }

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
