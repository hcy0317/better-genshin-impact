using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;
using Vanara.PInvoke;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class LegacyPathingMacroTests
{
    [Theory]
    [InlineData("early")]
    [InlineData("stale")]
    [InlineData("session")]
    [InlineData("frequency")]
    [InlineData("pixels")]
    [InlineData("partial")]
    [InlineData("uncertain")]
    [InlineData("non-f")]
    [InlineData("unknown-scene")]
    [InlineData("deadline")]
    [InlineData("no-frame")]
    public async Task InteractionContextRejectsUnrelatedFramesAndIncompleteReceipts(string fault)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var logger = new InteractionLogger();
        var evidence = new PathingMacroEvidence("route=seed node=2", "keypress(f)", clock);
        using var before = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        evidence.Capture(before, "entry", new(PathingMacroScene.World, before.FrameStamp), logger);
        evidence.Boundary(0, clock.GetTimestamp() + clock.TimestampFrequency, clock.GetTimestamp());
        evidence.Input(new(PathingMacroInputKind.KeyUp, fault == "non-f" ? User32.VK.VK_W : User32.VK.VK_F),
            new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp())
            { NativeRequested = 1, NativeSubmitted = fault == "partial" ? 0 : 1, NativeUncertain = fault == "uncertain" });
        clock.Advance(TimeSpan.FromMilliseconds(fault == "early" ? 30 : fault == "deadline" ? 1100 : 100));
        if (fault == "session") source.Restart();
        using var after = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.White), 0, 0) { FrameStamp = source.Next() };
        if (fault == "frequency") after.FrameStamp = after.FrameStamp with { TimestampFrequency = clock.TimestampFrequency * 2 };
        if (fault == "stale") clock.Advance(TimeSpan.FromMilliseconds(200));
        if (fault != "no-frame") evidence.Capture(after, "scene-transition",
            new(fault == "unknown-scene" ? PathingMacroScene.Unknown : PathingMacroScene.World,
                fault == "pixels" ? before.FrameStamp : after.FrameStamp), logger);
        evidence.End();
        await scope.DisposeAsync();
        Assert.DoesNotContain(saved, item => item.Phase == "path-interaction-context");
        if (fault == "no-frame") Assert.Contains(logger.Messages, message => message.Contains("feedback-not-observed-before-macro-end"));
    }

    private sealed class InteractionLogger : ILogger
    {
        internal readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
            => Messages.Add(formatter(state, error));
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("full")]
    [InlineData("failed")]
    public async Task RecordedSeedInteractionKeepsSettledExistingFramesAfterMacroEndWithoutChangingExecution(string mode)
    {
        async Task<(string[] Inputs, int Observations, long End)> Run(bool collect)
        {
            var saved = new List<(DiagnosticEvidence Evidence, byte Pixel)>();
            await using var scope = collect ? new DiagnosticEvidenceScope((item, pixels) =>
            {
                if (mode == "failed") throw new IOException("test-writer-failure");
                saved.Add((item, pixels.At<Vec3b>(0, 0).Item0));
                return Task.CompletedTask;
            }, maximumBytes: mode == "full" ? 1 : null) : null;
            var device = new MacroReplay { Scene = PathingMacroScene.World };
            var io = new InteractionEvidenceIo(device);
            using var session = new PathingMacroSession(io);
            var plan = LegacyPathingMacroPlan.Create(CombatScriptParser.ParseContext("keypress(f),w(0.3),keypress(f)", false), ["钟离"]);
            var result = await session.ExecuteAsync(plan, (_, _) => throw new Exception("unexpected native segment"), default);
            Assert.True(result.CanContinue);
            Assert.Equal(6, device.Inputs.Count);
            var execution = (device.Inputs.ToArray(), device.Observations, device.Clock.GetTimestamp());
            // CaptureContent is the existing next-node producer; the collector must not capture or wait for it.
            for (byte i = 1; i <= 5; i++)
            {
                device.Time.Advance(TimeSpan.FromSeconds(1));
                using var frame = new GameCaptureFrame(new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(i, 0, 0)), stamp: device.NextFrame());
                using var content = new CaptureContent(frame, 0, 50, new FakeSystemInfo(new(0, 0, 1920, 1080), 1));
            }
            if (scope != null)
            {
                await scope.DisposeAsync();
                if (mode == "normal")
                {
                    var context = saved.Where(item => item.Evidence.Phase == "path-interaction-context").ToArray();
                    Assert.Equal(2, context.Count(item => item.Evidence.Window?.RelativeIndex == 0));
                    Assert.Contains(context, item => item.Pixel >= 4 && item.Evidence.Window?.RelativeSeconds > 3);
                    Assert.All(context, item =>
                    {
                        Assert.Equal(DiagnosticEvidencePriority.Routine, item.Evidence.Priority);
                        Assert.Contains("node=2", item.Evidence.Fields!["interaction:context"]);
                        Assert.Contains("native=1/1", item.Evidence.Fields["interaction:receipt"]);
                        Assert.Equal("not-observed", item.Evidence.Fields["interaction:outcome"]);
                    });
                }
                else Assert.Empty(saved);
            }
            return execution;
        }
        var baseline = await Run(false);
        var observed = await Run(true);
        Assert.Equal(baseline.Inputs, observed.Inputs);
        Assert.Equal(baseline.Observations, observed.Observations);
        Assert.Equal(baseline.End, observed.End);
    }

    // Same evidence callbacks as NativePathingMacroIo; only screen and input devices are replaced.
    private sealed class InteractionEvidenceIo(MacroReplay device) : IPathingMacroIo
    {
        private PathingMacroEvidence? _evidence;
        public TimeProvider Clock => device.Clock;
        public CombatInputCoordinator Coordinator => device.Coordinator;
        public User32.VK Map(User32.VK key) => device.Map(key);
        public Task Delay(int milliseconds, CancellationToken ct) => device.Delay(milliseconds, ct);
        public void BeginEvidence(IReadOnlyList<CombatCommand> commands) => _evidence = DiagnosticEvidenceScope.Current == null ? null :
            new("route=001【激活程序】稻妻大炮2.json node=2", "keypress(f),w(0.3),keypress(f)", Clock);
        public void EvidenceBoundary(int index, long deadline, long fence) => _evidence?.Boundary(index, deadline, fence);
        public void EndEvidence() { _evidence?.End(); _evidence = null; }
        public void FailEvidence(Exception error) => _evidence?.Failed(error);
        public PathingMacroObservation Observe(string phase = "boundary")
        {
            var observation = device.Observe(phase);
            using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = observation.Source };
            _evidence?.Capture(frame, phase, observation);
            return observation;
        }
        public CombatBattleHostInputResult Send(PathingMacroInput input, Action admit)
        {
            var receipt = device.Send(input, admit);
            _evidence?.Input(input, receipt);
            return receipt;
        }
    }
}
