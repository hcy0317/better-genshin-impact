using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DiagnosticEvidenceTimeWindowTests
{
    [Fact]
    public async Task CompletedStormTailsReleaseTheirSlotsBeforeTheRunEnds()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; },
            maxPendingWindows: 1, queueCapacity: 256);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var incident = 0; incident < 3; incident++)
        {
            for (var second = 0; second <= 18; second++)
            {
                frame.FrameStamp = source.Next();
                scope.ObserveExistingFrame(frame);
                if (second is 10 or 12)
                    scope.RequestWindow($"storm-{incident}", "warning", frame.FrameStamp, "same");
                clock.Advance(TimeSpan.FromSeconds(1));
            }
        }
        await scope.DisposeAsync();
        for (var incident = 0; incident < 3; incident++)
            Assert.Contains(saved, item => item.Request == $"storm-{incident}" &&
                item.Window is { Occurrence: "last", RelativeSeconds: 5 });
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public async Task RoutineFramesCloneOnlySparseSamplesButExplicitFaultPinsExactPixels(int fps)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 0; i < fps * 10; i++)
        {
            frame.FrameStamp = source.Next();
            scope.ObserveExistingFrame(frame);
            clock.Advance(TimeSpan.FromSeconds(1d / fps));
        }
        Assert.InRange(scope.HistoryCloneCount, 9, 11);
        scope.RequestWindowFromFrame("exact", "fault", frame, "unsampled trigger");
        Assert.InRange(scope.HistoryCloneCount, 10, 12);
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Window?.RelativeIndex == 0 && item.Source == frame.FrameStamp);
    }

    [Fact]
    public async Task FailedUiInputKeepsItsReceiptAndRethrowsTheOriginalError()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        scope.ObserveExistingFrame(frame);
        var error = new IOException("fixture");
        var dispatcher = new Fischless.WindowsInput.WindowsInputMessageDispatcher(null, _ => throw error, () => 0);
        var actual = await Assert.ThrowsAsync<IOException>(() => BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync<bool>(
            "exit-domain", TimeSpan.FromSeconds(30), default, op => op.InvokeActionAsync(
                BetterGenshinImpact.GameTask.Common.Ui.UiAction.ConfirmDomainExit, () =>
                { dispatcher.DispatchInput(new Vanara.PInvoke.User32.INPUT[1]); return Task.FromResult(true); }, 1, 2)));
        Assert.Same(error, actual);
        await scope.DisposeAsync();
        var input = Assert.Single(saved).Fields!["input:last"];
        Assert.Contains("status=Unknown", input);
        Assert.Contains("nativeRequested=1", input);
        Assert.Contains("nativeSubmitted=0", input);
        Assert.Contains("requestedAt=", input);
        Assert.Contains("completedAt=", input);
        Assert.Contains("frequency=", input);
        Assert.Contains("reason=IOException", input);
    }

    [Fact]
    public void CombatRecognitionIdentityUsesEffectiveWhitelistedValues()
    {
        var config = new BetterGenshinImpact.GameTask.AutoFight.Model.VisualRecognitionConfig(
            TargetingDetectionInterval: 75, DrawRecognitionResults: true, LockLostWaitTime: 0.7);
        var identity = BetterGenshinImpact.GameTask.AutoFight.Model.AvatarRecognition.DescribeEvidenceConfig(config, false);
        Assert.Contains("intervalMs=75", identity);
        Assert.Contains("draw=False", identity);
        Assert.Contains("lockLostWaitSeconds=0.7", identity);
        Assert.Contains("damageMode=Color", identity);
    }

    [Fact]
    public async Task SameNamedRouteKeepsDistinctSourcesAndSelectorsWithoutAbsolutePaths()
    {
        var logger = new RecordingLogger();
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        scope.ObserveExistingFrame(frame);
        scope.RequestWindow("identity", "fault", frame.FrameStamp, "", logger);
        scope.RegisterIdentity("route", "same.json", @"C:\Users\private-person\first\same.json", "unknown");
        scope.RegisterIdentity("route", "same.json", @"C:\Users\private-person\second\same.json", "unknown");
        scope.RegisterIdentity("map-config", "same.json", "layer=1", "selector");
        scope.RegisterIdentity("map-config", "same.json", "layer=2", "selector");
        await scope.DisposeAsync();
        var report = Assert.Single(logger.Messages.Where(m => m.StartsWith("EVIDENCE_RUN_IDENTITIES ")));
        Assert.DoesNotContain("private-person", report);
        Assert.Contains("layer=1", report);
        Assert.Contains("layer=2", report);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(report, "path-sha256:").Count);
    }

    [Fact]
    public async Task StormTailRetainsItsWindowWhenRunContinuesTwentySecondsAfterLastWarning()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var second = 0; second <= 32; second++)
        {
            frame.FrameStamp = source.Next();
            scope.ObserveExistingFrame(frame);
            if (second is 10 or 12) scope.RequestWindow("storm", "warning", frame.FrameStamp, "same");
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        await scope.DisposeAsync();
        var tail = saved.Where(item => item.Window?.Occurrence == "last").ToArray();
        Assert.Contains(tail, item => item.Window!.RelativeSeconds == -10);
        Assert.Contains(tail, item => item.Window!.RelativeSeconds == 0);
        Assert.Contains(tail, item => item.Window!.RelativeSeconds == 5);
    }

    [Fact]
    public async Task LongCombatDetailCannotTruncateIndependentFilterAndProgressFields()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var recognition = new BetterGenshinImpact.GameTask.AutoFight.SeekRecognitionDiagnostics();
        recognition.Rejections.Add("health:width/indicator:shape", 4);
        var observation = new BetterGenshinImpact.GameTask.AutoFight.CombatBattleObservation(frame.FrameStamp,
            Guid.NewGuid(), BetterGenshinImpact.GameTask.AutoFight.CombatObservationQuality.Available, null, 1920, 1080)
        { Recognition = recognition };
        var trace = new BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostTrace(observation.BattleId, "episode", "done", "unconfirmed",
            BetterGenshinImpact.GameTask.AutoFight.CombatBattleHostResult.Unconfirmed, observation, 24, 12, 45, 0, true, "terminal")
        { LastProgress = new(frame.FrameStamp, "health-progress", null, null, frame.FrameStamp.CapturedTimestamp) };
        scope.ObserveExistingFrame(frame);
        scope.RequestWindow("combat", "combat-terminal", frame.FrameStamp, new string('x', 5000),
            fields: BetterGenshinImpact.GameTask.AutoFight.NativeCombatBattleHostIo.EvidenceFields(trace));
        await scope.DisposeAsync();
        var evidence = Assert.Single(saved);
        Assert.Equal(2048, evidence.Detail.Length);
        Assert.Contains("health:width/indicator:shape:4", evidence.Fields!["filterReasons"]);
        Assert.Contains("kind=health-progress", evidence.Fields["lastProgressSource"]);
        Assert.Contains("reason=unconfirmed", evidence.Fields["termination"]);
    }

    [Fact]
    public async Task RepeatedIncidentKeepsFirstChangedAndLastSourceWithItsOwnCount()
    {
        var saved = new List<DiagnosticEvidence>();
        var logger = new RecordingLogger();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var stamps = new List<CaptureFrameStamp>();
        for (var i = 0; i < 20; i++)
        {
            frame.FrameStamp = source.Next();
            stamps.Add(frame.FrameStamp);
            scope.ObserveExistingFrame(frame);
            scope.RequestWindowFromFrame("storm", "warning", frame, i < 10 ? "stalled" : "worsened", logger);
            clock.Advance(TimeSpan.FromMilliseconds(250));
        }
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Source == stamps[0]);
        Assert.Contains(saved, item => item.Source == stamps[10] && item.Detail.Contains("worsened"));
        Assert.Contains(saved, item => item.Source == stamps[19] && item.Detail.Contains("worsened"));
        var summary = Assert.Single(logger.Messages.Where(item => item.StartsWith("EVIDENCE_WINDOW_SUMMARY ")));
        Assert.Contains("\"Occurrences\":20", summary);
        Assert.Contains("\"Changes\":1", summary);
        Assert.InRange(saved.Count, 3, 32);
    }

    [Fact]
    public async Task UiFailureKeepsEarlyMilestonesAndActionsAfterMoreThanFourStates()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        scope.ObserveExistingFrame(frame);
        await Assert.ThrowsAsync<IOException>(() => BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync<bool>(
            "exit-domain", TimeSpan.FromSeconds(30), default, op =>
            {
                op.Observe("challenge-complete", "observed-completion", 1);
                op.Observe("reward", "reward-visible", 2);
                op.Action(BetterGenshinImpact.GameTask.Common.Ui.UiAction.RequestDomainExit, true, 1, 2);
                op.Action(BetterGenshinImpact.GameTask.Common.Ui.UiAction.ConfirmDomainExit, false, 1, 2);
                for (var i = 3; i < 12; i++) op.Observe("loading", "loading-" + i, i);
                throw new IOException("fixture");
            }));
        await scope.DisposeAsync();
        var json = Newtonsoft.Json.Linq.JObject.FromObject(Assert.Single(saved)).ToString();
        Assert.Contains("observed-completion", json);
        Assert.Contains("reward-visible", json);
        Assert.Contains("RequestDomainExit", json);
        Assert.Contains("ConfirmDomainExit", json);
        Assert.Contains("applied=False", json);
        Assert.Contains("loading-11", json);
    }

    [Fact]
    public async Task ErrorEvidenceReplacesQueuedWarningsWithoutIncreasingCapacity()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope(async (item, _) =>
        { entered.TrySetResult(); await release.Task; saved.Add(item); }, queueCapacity: 2);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        try
        {
            Assert.True(scope.TryCapture(frame, "active", "fault", "", priority: DiagnosticEvidencePriority.Warning));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 2; i++)
                scope.TryCapture(frame, "warning-" + i, "fault", "", priority: DiagnosticEvidencePriority.Warning);
            Assert.True(scope.TryCapture(frame, "error", "combat-terminal", "", priority: DiagnosticEvidencePriority.Error));
        }
        finally { release.TrySetResult(); }
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Request == "error");
        Assert.InRange(saved.Count, 2, 3);
    }

    [Fact]
    public async Task WindowKeepsTheTriggeringTaskAndScriptIdentityWhenLaterFramesHaveNoOwner()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = source.Next() };
        using (BetterGenshinImpact.GameTask.TaskExecutionScope.BeginOwned())
        using (new BetterGenshinImpact.Core.Script.ScriptAsyncLifetime(default))
        {
            scope.ObserveExistingFrame(frame);
            Assert.True(scope.RequestWindow("identity", "deadline", frame.FrameStamp, ""));
        }
        clock.Advance(TimeSpan.FromSeconds(5));
        frame.FrameStamp = source.Next();
        scope.ObserveExistingFrame(frame);
        await scope.DisposeAsync();
        var json = saved.Select(Newtonsoft.Json.Linq.JObject.FromObject).ToArray();
        Assert.Equal(2, json.Length);
        Assert.All(json, item => Assert.False(string.IsNullOrEmpty((string?)item["TaskInstanceId"])));
        Assert.All(json, item => Assert.False(string.IsNullOrEmpty((string?)item["ScriptInstanceId"])));
        Assert.Single(json.Select(item => (string?)item["TaskInstanceId"]).Distinct());
        Assert.Single(json.Select(item => (string?)item["ScriptInstanceId"]).Distinct());
    }

    [Fact]
    public async Task FaultWindowReclaimsQueuedRoutinePixelsUnderMemoryPressure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope(async (item, _) =>
        { entered.TrySetResult(); await release.Task; saved.Add(item); }, maximumMemoryBytes: 128);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 0; i < 3; i++)
        {
            frame.FrameStamp = source.Next();
            scope.ObserveExistingFrame(frame);
            clock.Advance(TimeSpan.FromSeconds(5));
        }
        try
        {
            Assert.True(scope.TryCapture(frame, "active", "routine", ""));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 64 && scope.TryCapture(frame, "routine-" + i, "routine", ""); i++) { }
            Assert.True(scope.RequestWindow("fault", "deadline", frame.FrameStamp, ""));
        }
        finally { release.TrySetResult(); }
        await scope.DisposeAsync();
        Assert.Equal(3, saved.Count(item => item.Window != null));
    }

    [Theory]
    [InlineData("exit-domain")]
    [InlineData("path-precise-recovery")]
    public async Task FailedUiTransitionsPinTheLatestSourceWithoutChangingTheFailure(string operation)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        scope.ObserveExistingFrame(frame);
        var failure = new InvalidOperationException("fixture");
        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync<bool>(operation, TimeSpan.FromSeconds(30), default,
                op => { op.Observe("overworld", "loading=false main=false", frame.FrameStamp.Sequence); throw failure; }));
        Assert.Same(failure, observed);
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Phase == operation && item.Source == frame.FrameStamp && item.Window != null);
    }

    [Fact]
    public async Task EndingWithoutFutureFramesReportsTheIncidentAndActualCoverage()
    {
        var logger = new RecordingLogger();
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        scope.ObserveExistingFrame(frame);
        scope.RegisterIdentity("strategy", "battle-17", "fixture.txt", "fixture-content-hash");
        Assert.True(scope.RequestWindow("attempt-17", "deadline", frame.FrameStamp, "fixture", logger));
        await scope.DisposeAsync();
        var report = Assert.Single(logger.Messages.Where(m => m.StartsWith("EVIDENCE_WINDOW_SUMMARY ")));
        Assert.Contains("attempt-17", report);
        Assert.Contains("IncidentId", report);
        Assert.Contains("no-next-frame-before-run-end", report);
        Assert.Contains("BeforeSeconds", report);
        Assert.Contains("WrittenFrames", report);
        Assert.Contains(logger.Messages, m => m.StartsWith("EVIDENCE_RUN_IDENTITIES ") && m.Contains("fixture-content-hash"));
    }

    private sealed class RecordingLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    public async Task FaultWindowRetainsTenSecondsBeforeAndFiveSecondsAfter(int fps)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var session = Guid.NewGuid();
        var epoch = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        CaptureFrameStamp At(int tick) => new(session, tick + 1, tick, fps, epoch.AddSeconds(tick / (double)fps));
        for (var tick = 0; tick <= 12 * fps; tick++)
        {
            frame.FrameStamp = At(tick);
            scope.ObserveExistingFrame(frame);
        }
        var anchor = frame.FrameStamp;
        Assert.True(scope.RequestWindow("skill-timeout", "deadline", anchor, "fixture"));
        for (var tick = 12 * fps + 1; tick <= 18 * fps; tick++)
        {
            frame.FrameStamp = At(tick);
            scope.ObserveExistingFrame(frame);
        }
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Source == At(2 * fps));
        Assert.Contains(saved, item => item.Source == anchor);
        Assert.Contains(saved, item => item.Source == At(17 * fps));
        Assert.All(saved, item => Assert.InRange(item.Source.CapturedTimestamp, 2 * fps, 17 * fps));
        Assert.InRange(saved.Count, 3, 34); // 有界稀疏样本，不承诺全帧率录像。
        Assert.Single(saved.Select(item => item.Window!.WindowId).Distinct());
    }
}
