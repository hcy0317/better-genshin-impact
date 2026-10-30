using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathSwimGateEvidenceTests
{
    [Fact]
    public async Task TwoFreshSwimFramesReportTheirOwnCandidateAndRefusalSources()
    {
        var log = new GateLogger();
        var replay = new PathReplay { DiagnosticLogger = log, MotionAt = _ => MotionStatus.Swim };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.Equal(new[] { "candidate", "rejected" }, log.Events.Select(x => (string)x["Event"]!));
        var first = log.Events[0];
        var refused = log.Events[1];
        Assert.Equal(0, first["Before"]);
        Assert.Equal(1, first["After"]);
        Assert.Equal(1, refused["Before"]);
        Assert.Equal(2, refused["After"]);
        Assert.True((long)first["Sequence"]! > (long)first["PreviousSequence"]!);
        Assert.Equal(first["Sequence"], refused["PreviousSequence"]);
        Assert.True((long)refused["Sequence"]! > (long)first["Sequence"]!);
        Assert.All(log.Events, e =>
        {
            Assert.Equal(true, e["Valid"]);
            Assert.Equal(true, e["SourceUsable"]);
            Assert.Equal(MotionStatus.Swim, e["Motion"]);
        });
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task ARealNormalObservationReportsTheCounterResetBeforeTheSameSwimRefusal()
    {
        var log = new GateLogger();
        // 就绪和初始定位先消费两帧；普通姿态放在第二张门禁观测，真实覆盖1→0。
        var replay = new PathReplay { DiagnosticLogger = log,
            MotionAt = frame => frame == 4 ? MotionStatus.Normal : MotionStatus.Swim };
        await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.Equal(new[] { "candidate", "reset", "candidate", "rejected" }, log.Events.Select(x => (string)x["Event"]!));
        var reset = log.Events[1];
        Assert.Equal(1, reset["Before"]);
        Assert.Equal(0, reset["After"]);
        Assert.Equal(MotionStatus.Normal, reset["Motion"]);
        Assert.Equal(true, reset["Valid"]);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task FailedDiagnosticLoggingPreservesTheOriginalSwimRejectionAndCleanup()
    {
        var replay = new PathReplay { DiagnosticLogger = new GateLogger { FailGate = true },
            MotionAt = _ => MotionStatus.Swim };
        var error = await Assert.ThrowsAsync<RetryException>(() => replay.Executor.MoveTo(replay.Point("fly")));
        Assert.Contains("游泳", error.Message);
        Assert.All(replay.Images, image => Assert.True(image.SrcMat.IsDisposed));
    }

    private sealed class GateLogger : ILogger
    {
        internal bool FailGate;
        internal readonly List<Dictionary<string, object?>> Events = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error,
            Func<TState, Exception?, string> formatter)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> fields) return;
            var values = fields.ToDictionary(x => x.Key, x => x.Value);
            if (values.TryGetValue("{OriginalFormat}", out var template) &&
                template is string text && text.StartsWith("PATH_SWIM_GATE "))
            {
                if (FailGate) throw new IOException("diagnostic logger failed");
                Events.Add(values);
            }
        }
    }
}
