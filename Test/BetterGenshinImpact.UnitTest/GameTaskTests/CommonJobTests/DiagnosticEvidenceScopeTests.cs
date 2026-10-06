using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DiagnosticEvidenceScopeTests
{
    [Fact]
    public async Task NewerSparseHistoryCannotReplaceAnExactOlderDecisionImage()
    {
        var saved = new List<(DiagnosticEvidence Evidence, byte Pixel)>();
        await using var scope = new DiagnosticEvidenceScope((item, image) =>
        { saved.Add((item, image.At<Vec3b>(0, 0).Item0)); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        using var decision = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(7, 0, 0)), 0, 0) { FrameStamp = source.Next() };
        scope.RememberExactFrame("party", "probe", decision);
        using var newer = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(9, 0, 0)), 0, 0) { FrameStamp = source.Next() };
        scope.ObserveExistingFrame(newer);
        Assert.True(scope.CaptureExactWindow("party", "probe", decision.FrameStamp, "party-terminal", "exact decision"));
        await scope.DisposeAsync();
        Assert.Contains(saved, sample => sample.Evidence.Source == decision.FrameStamp && sample.Pixel == 7);
    }
    [Fact]
    public async Task ExactRetentionReplacementPreservesSourceOwnershipAndMemoryBudget()
    {
        var source = new CaptureFrameSource();
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask, maximumMemoryBytes: 32);
        using var first = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        using var tooLarge = new ImageRegion(new Mat(4, 4, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        scope.RememberExactFrame("macro", "a", first);
        scope.RememberExactFrame("macro", "a", tooLarge);
        Assert.False(scope.CaptureExactWindow("macro", "a", tooLarge.FrameStamp, "terminal", "must not relabel old pixels"));
        scope.ForgetExactFrame("macro", "a");
        Assert.False(scope.CaptureExactWindow("macro", "a", first.FrameStamp, "terminal", "released"));
        Assert.False(first.SrcMat.IsDisposed);
        Assert.False(tooLarge.SrcMat.IsDisposed);
    }
    [Fact]
    public async Task DirectCaptureFieldsAreBoundedSnapshotsIndependentOfCallerMutation()
    {
        DiagnosticEvidence? saved = null;
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved = item; return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var fields = new Dictionary<string, string> { ["source"] = "original" };
        for (var i = 0; i < 20; i++) fields[i + new string('k', 80)] = new string('v', 800);
        Assert.True(scope.TryCapture(frame, "attempt", "deadline", "existing frame", fields: fields));
        fields["source"] = "changed";
        fields.Clear();
        await scope.DisposeAsync();
        Assert.Equal("original", saved!.Fields!["source"]);
        Assert.Equal(16, saved.Fields.Count);
        Assert.All(saved.Fields, pair => { Assert.InRange(pair.Key.Length, 1, 64); Assert.InRange(pair.Value.Length, 0, 512); });
    }

    [Fact]
    public async Task RepeatedFaultPhaseDoesNotInventAnotherMissingBeforeFrame()
    {
        var logger = new EvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        for (var i = 0; i < 20; i++) scope.CaptureFault(frame, "skill", "attempt", "unconfirmed", "", logger);
        await scope.DisposeAsync();
        var summary = Assert.Single(logger.Messages.Where(message => message.StartsWith("EVIDENCE_DRAINED ")));
        Assert.Contains("missingBefore=1", summary);
        Assert.Contains("duplicateSuppressed=19", summary);
        Assert.Single(logger.Messages.Where(message => message.Contains("reason=before-unavailable")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SlowWriterLeavesTerminalCapacityWithoutBlockingTheProducer(bool queuePressure)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var written = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope(async (item, _) =>
        {
            written.Add(item);
            entered.TrySetResult();
            await release.Task;
        }, queueCapacity: queuePressure ? 4 : 64, maximumMemoryBytes: queuePressure ? 256L * 1024 * 1024 : 1200);
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        try
        {
            Assert.True(scope.TryCapture(frame, "active", "ordinary", ""));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var count = 0;
            while (count < 64 && scope.TryCapture(frame, "queued-" + count, "ordinary", "")) count++;
            Assert.InRange(count, 1, 4);
            Assert.True(scope.TryCapture(frame, "battle", "combat-terminal", "unconfirmed"));
        }
        finally { release.TrySetResult(); }
        await scope.DisposeAsync();
        Assert.Single(written.Where(item => item.Phase == "combat-terminal"));
        Assert.InRange(written.Count, 2, queuePressure ? 5 : 4);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OrdinaryQuotaPressureKeepsRoomForOneTerminalFrame(bool imageLimit)
    {
        var written = System.Threading.Channels.Channel.CreateUnbounded<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) =>
        {
            written.Writer.TryWrite(item);
            return Task.CompletedTask;
        }, maxImages: imageLimit ? 8 : 100, maximumBytes: imageLimit ? null : 96);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var accepted = 0;
        while (accepted < 100 && scope.TryCapture(frame, "ordinary-" + accepted, "ordinary", ""))
        {
            accepted++;
            await written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.InRange(accepted, 1, 7);
        Assert.True(scope.TryCapture(frame, "battle", "combat-terminal", "unconfirmed"));
        var terminal = await written.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("combat-terminal", terminal.Phase);
        Assert.InRange(terminal.Sequence, 2, 8);
    }

    [Fact]
    public async Task AnEvictedAnchorCannotRelabelLaterHistoryAsItsImmediateAfterFrames()
    {
        var saved = new List<DiagnosticEvidence>();
        var logger = new EvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var anchor = source.Next();
        frame.FrameStamp = anchor;
        scope.ObserveExistingFrame(frame);
        for (var i = 0; i < 22; i++) { clock.Advance(TimeSpan.FromSeconds(1)); frame.FrameStamp = source.Next(); scope.ObserveExistingFrame(frame); }
        Assert.False(scope.RequestWindow("late-anchor", "decision", anchor, "", logger));
        await scope.DisposeAsync();
        Assert.Empty(saved);
        Assert.Contains(logger.Messages, message => message.Contains("window-anchor-unavailable"));
    }

    [Fact]
    public async Task BudgetRejectedRememberedBeforeFrameCountsAsMissingBefore()
    {
        var logger = new EvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask, maxImages: 1);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        Assert.True(scope.RememberBefore(frame, "skill", "attempt", "before"));
        Assert.True(scope.TryCapture(frame, "other", "terminal", "", logger));
        scope.CaptureFault(frame, "skill", "attempt", "failed", "", logger);
        await scope.DisposeAsync();
        var drained = Assert.Single(logger.Messages.Where(message => message.StartsWith("EVIDENCE_DRAINED ")));
        Assert.Contains("dropped=2", drained);
        Assert.Contains("missingBefore=1", drained);
    }

    [Fact]
    public async Task MissingOnlyWindowStillPublishesOneFinalSummaryAndLateCallsDoNotExtendIt()
    {
        var logger = new EvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope((_, _) => throw new InvalidOperationException("no image"));
        Assert.False(scope.RequestWindow("missing", "decision", default, "", logger));
        await scope.DisposeAsync();
        var drained = Assert.Single(logger.Messages.Where(message => message.StartsWith("EVIDENCE_DRAINED ")));
        Assert.Contains("dropped=1", drained);
        Assert.Contains("missingBefore=1", drained);
        Assert.Contains("capture:source-unknown\":1", drained);
        var count = logger.Messages.Count;
        Assert.False(scope.RequestWindow("late", "decision", default, "", logger));
        Assert.Equal(count, logger.Messages.Count);
    }

    [Fact]
    public async Task DelayedOldSourceFrameCannotInterruptTheCurrentSourceWindow()
    {
        var saved = new List<DiagnosticEvidence>();
        var logger = new EvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var old = new CaptureFrameStamp(Guid.NewGuid(), 1, 100, 1000, DateTimeOffset.UtcNow);
        var current = new CaptureFrameStamp(Guid.NewGuid(), 1, 200, 1000, DateTimeOffset.UtcNow);
        frame.FrameStamp = old;
        scope.ObserveExistingFrame(frame);
        frame.FrameStamp = current;
        scope.ObserveExistingFrame(frame);
        Assert.True(scope.RequestWindow("current", "decision", current, "", logger));
        frame.FrameStamp = old with { Sequence = 2, CapturedTimestamp = 150 };
        scope.ObserveExistingFrame(frame);
        frame.FrameStamp = current with { Sequence = 2, CapturedTimestamp = 1200 };
        scope.ObserveExistingFrame(frame);
        await scope.DisposeAsync();
        Assert.Equal(new[] { 0, 1 }, saved.Select(item => item.Window!.RelativeIndex));
        Assert.All(saved, item => Assert.Equal(current.SessionId, item.Source.SessionId));
        Assert.DoesNotContain(logger.Messages, message => message.Contains("capture-source-changed"));
    }

    [Fact]
    public async Task SourceChangeSettlesMissingAfterFramesAndWindowLimitCannotBeEvaded()
    {
        var saved = new List<DiagnosticEvidence>();
        var logger = new EvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; }, maxWindows: 1);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        scope.ObserveExistingFrame(frame);
        Assert.True(scope.RequestWindow("one", "decision", frame.FrameStamp, "", logger));
        scope.ObserveExistingFrame(frame); // 同一帧不能填后窗口。
        frame.FrameStamp = new CaptureFrameSource().Next();
        scope.ObserveExistingFrame(frame);
        Assert.False(scope.RequestWindow("two", "decision", frame.FrameStamp, "", logger));
        await scope.DisposeAsync();
        Assert.Single(saved);
        var drained = Assert.Single(logger.Messages.Where(message => message.StartsWith("EVIDENCE_DRAINED ")));
        Assert.Contains("dropped=3", drained); // 前窗不足、切源后窗不足、窗口预算拒绝；不虚构缺失帧数。
        Assert.Contains("capture:capture-source-changed\":1", drained);
        Assert.Contains("capture:window-budget\":1", drained);
    }

    [Fact]
    public async Task MemoryPressureReportsHolesWithoutBorrowingOrDisposingCallerPixels()
    {
        var logger = new EvidenceLogger();
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; }, maximumMemoryBytes: 24);
        var source = new CaptureFrameSource();
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 0; i < 4; i++) { frame.FrameStamp = source.Next(); scope.ObserveExistingFrame(frame); }
        Assert.True(scope.RequestWindowFromFrame("memory", "decision", frame, "", logger));
        await scope.DisposeAsync();
        var retained = Assert.Single(saved); // 故障预留仍可保存一张真实历史帧，但不能冒充缺失锚点。
        Assert.True(retained.Window!.RelativeIndex < 0);
        Assert.NotEqual(frame.FrameStamp, retained.Source);
        Assert.False(frame.SrcMat.IsDisposed);
        Assert.Contains(logger.Messages, message => message.Contains("memory-budget"));
    }

    [Theory]
    [InlineData("map-area-selection")]
    [InlineData("return-main")]
    public async Task SuccessfulUiHandoffsLeaveCapacityForTerminalEvidence(string name)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; }, maxImages: 2);
        var source = new CaptureFrameSource();
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var operation = 0; operation < 20; operation++)
            await BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync(name, TimeSpan.FromSeconds(10), default, _ =>
            {
                for (var i = 0; i < 11; i++) { frame.FrameStamp = source.Next(); scope.ObserveExistingFrame(frame); }
                return Task.FromResult(true);
            });
        Assert.True(scope.TryCapture(frame, "battle", "combat-terminal", "unconfirmed"));
        await scope.DisposeAsync();
        Assert.Equal("combat-terminal", Assert.Single(saved).Phase);
    }

    [Fact]
    public async Task WindowUsesTimedExistingSamplesWithoutChangingTheirSource()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 0; i < 11; i++) { frame.FrameStamp = source.Next(); scope.ObserveExistingFrame(frame); clock.Advance(TimeSpan.FromSeconds(1)); }
        var anchor = frame.FrameStamp;
        Assert.True(scope.RequestWindow("decision", "handoff", anchor, "fixture"));
        for (var i = 0; i < 10; i++) { frame.FrameStamp = source.Next(); scope.ObserveExistingFrame(frame); clock.Advance(TimeSpan.FromSeconds(1)); }
        await scope.DisposeAsync();
        saved = saved.OrderBy(item => item.Window!.RelativeIndex).ToList();
        Assert.Equal(Enumerable.Range(1, 16).Select(i => (long)i), saved.Select(item => item.Source.Sequence));
        Assert.Equal(Enumerable.Range(-10, 16), saved.Select(item => item.Window!.RelativeIndex));
        Assert.All(saved, item => { Assert.Equal(anchor, item.Window!.Anchor); Assert.Equal(anchor.SessionId, item.Source.SessionId); });
        Assert.Single(saved.Select(item => item.Window!.WindowId).Distinct());
        Assert.False(frame.SrcMat.IsDisposed);
    }

    [Theory]
    [InlineData("combat-terminal")]
    [InlineData("deadline")]
    public async Task TerminalWindowSavesItsAnchorBeforeHistoryConsumesTheLastSlot(string phase)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; }, maxImages: 1);
        var source = new CaptureFrameSource();
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 0; i < 11; i++) { frame.FrameStamp = source.Next(); scope.ObserveExistingFrame(frame); }
        Assert.True(scope.RequestWindowFromFrame("battle", phase, frame, ""));
        await scope.DisposeAsync();
        var terminal = Assert.Single(saved);
        Assert.Equal(0, terminal.Window!.RelativeIndex);
        Assert.Equal(frame.FrameStamp, terminal.Source);
    }

    [Fact]
    public async Task OrdinaryWindowsCannotExhaustTerminalWindowAdmission()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; },
            maxWindows: 2, maxPendingWindows: 2);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        scope.ObserveExistingFrame(frame);
        Assert.True(scope.RequestWindow("ordinary", "decision", frame.FrameStamp, ""));
        Assert.False(scope.RequestWindow("ordinary-2", "decision", frame.FrameStamp, ""));
        Assert.True(scope.RequestWindow("battle", "combat-terminal", frame.FrameStamp, ""));
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Phase == "combat-terminal" && item.Window!.RelativeIndex == 0);
    }

    [Fact]
    public async Task DrainReportsFormattedCountsAndEveryMissingReasonExactlyOnce()
    {
        var logger = new EvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope((_, _) => throw new IOException("offline disk failure"), maxImages: 1);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        scope.CaptureFault(frame, "skill", "attempt", "failed", "", logger);
        Assert.False(scope.TryCapture(frame, "attempt", "failed", "duplicate", logger));
        using var unknown = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        Assert.False(scope.TryCapture(unknown, "unknown", "failed", "", logger));
        Assert.False(scope.RequestFrame("owner", "over-budget", "terminal", frame.FrameStamp, "", logger));
        await scope.DisposeAsync();
        var drained = Assert.Single(logger.Messages.Where(message => message.StartsWith("EVIDENCE_DRAINED ")));
        Assert.Contains("accepted=1", drained);
        Assert.Contains("dropped=3", drained);
        Assert.Contains("missingBefore=1", drained);
        Assert.Contains("writeFailures=1", drained);
        Assert.Contains("duplicateSuppressed=1", drained);
        Assert.DoesNotContain("{Accepted}", drained);
        const string marker = " missingReasons=";
        Assert.Contains(marker, drained);
        var reasons = Newtonsoft.Json.Linq.JObject.Parse(drained[(drained.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
        Assert.Equal(4, reasons.Properties().Sum(property => (int)property.Value));
        Assert.Equal(1, (int)reasons["capture:before-unavailable"]!);
        Assert.Equal(1, (int)reasons["write:failed"]!);
    }

    [Fact]
    public async Task LowHealthRecoveryUsesTheBorrowedFrameOnceAndLeavesOwnershipWithCaller()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        for (var i = 0; i < 2; i++)
            BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.CaptureLowHpRecoveryEvidence(frame, "route=fixture node=6", Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        Assert.False(frame.SrcMat.IsDisposed);
        await scope.DisposeAsync();
        Assert.Equal("before-recovery", Assert.Single(saved).Phase);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingTerminalCapturesOnceButRetainedEvidenceNeedsNoNewFrame(bool retained)
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        var before = source.Next();
        var logger = new EvidenceLogger();
        if (retained)
        {
            scope.RequestFrame("owner", "request", "search-start", before, "", logger);
            using var old = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
            scope.CaptureRequestedFrames("owner", old);
        }
        scope.RequestFrame("owner", "request", "terminal", before, "original failure", logger);
        var captures = 0;
        ImageRegion? captured = null;
        ImageRegion Capture()
        {
            captures++;
            return captured = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        }
        scope.CapturePendingTerminal("owner", "request", Capture);
        scope.CapturePendingTerminal("owner", "request", Capture);
        await scope.DisposeAsync();
        Assert.Equal(retained ? 0 : 1, captures);
        Assert.Single(saved.Where(item => item.Phase == "terminal"));
        if (captured != null) Assert.True(captured.SrcMat.IsDisposed);
    }

    [Fact]
    public async Task PendingTerminalCaptureFailureCannotEscapeOrRetry()
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var logger = new EvidenceLogger();
        scope.RequestFrame("owner", "request", "terminal", new CaptureFrameSource().Next(), "original failure", logger);
        var calls = 0;
        ImageRegion? Fail() { calls++; throw new IOException("capture unavailable"); }
        scope.CapturePendingTerminal("owner", "request", Fail);
        scope.CapturePendingTerminal("owner", "request", Fail);
        Assert.Equal(1, calls);
        Assert.Contains(logger.Messages, message => message.Contains("terminal-capture-failed"));
    }

    [Fact]
    public async Task DedupeMetadataEvictionDoesNotLimitTotalCaptures()
    {
        var saved = System.Threading.Channels.Channel.CreateUnbounded<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) =>
        { saved.Writer.TryWrite(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var logger = new EvidenceLogger();
        for (var i = 0; i <= 1024; i++)
        {
            Assert.True(scope.TryCapture(frame, "request-" + i, "terminal", "debug", logger));
            await saved.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(scope.TryCapture(frame, "request-1024", "terminal", "recent duplicate", logger));
        Assert.True(scope.TryCapture(frame, "request-0", "terminal", "old metadata evicted", logger));
        var last = await saved.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1026, last.Sequence);
        await scope.DisposeAsync();
        Assert.Contains(logger.Messages, message => message.Contains("imageLimit=8192") && message.Contains("byteLimit=unlimited"));
    }

    [Fact]
    public async Task SlowDiskStillUsesABoundedQueueAndReportsPressureWithoutBlocking()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var images = new List<Mat>();
        await using var scope = new DiagnosticEvidenceScope(async (_, image) =>
        {
            images.Add(image);
            entered.TrySetResult();
            await release.Task;
        }, queueCapacity: 4);
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var logger = new EvidenceLogger();
        try
        {
            Assert.True(scope.TryCapture(frame, "active", "first", "", logger));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 3; i++) Assert.True(scope.TryCapture(frame, "queued-" + i, "first", "", logger));
            var rejected = Task.Run(() => scope.TryCapture(frame, "overflow", "first", "", logger));
            Assert.False(await rejected.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(logger.Messages, message => message.Contains("writer-queue-full"));
            Assert.True(scope.TryCapture(frame, "terminal", "terminal", "", logger));
        }
        finally { release.TrySetResult(); }
        await scope.DisposeAsync();
        Assert.Equal(5, images.Count);
        Assert.All(images, image => Assert.True(image.IsDisposed));
    }

    [Fact]
    public async Task DefaultScopeKeepsBeforeAndTerminalEvidenceAfter128MiB()
    {
        var saved = System.Threading.Channels.Channel.CreateUnbounded<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) =>
        { saved.Writer.TryWrite(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        using var frame = new ImageRegion(new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 0; i < 24; i++)
        {
            frame.FrameStamp = source.Next();
            Assert.True(scope.TryCapture(frame, "request-" + i, "first", "debug"), $"第{i + 1}张因旧字节配额被拒绝");
            await saved.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        frame.FrameStamp = source.Next();
        Assert.True(scope.RememberBefore(frame, "skill", "late-skill", "before"));
        scope.CaptureFault(frame, "skill", "late-skill", "unconfirmed", "after");
        await saved.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await saved.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var logger = new EvidenceLogger();
        Assert.True(scope.RequestFrame("battle", "late-host", "search-start", frame.FrameStamp, "search", logger));
        frame.FrameStamp = source.Next();
        scope.CaptureRequestedFrames("battle", frame);
        await saved.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(scope.RequestFrame("battle", "late-host", "terminal", frame.FrameStamp, "stop", logger));
        var terminal = await saved.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("terminal", terminal.Phase);
        Assert.Equal(frame.FrameStamp, terminal.Source);
        Assert.DoesNotContain(logger.Messages, message => message.Contains("run-budget"));
    }

    [Fact]
    public async Task DefaultScopeKeepsCapturingAfterSixteenRequests()
    {
        var saved = System.Threading.Channels.Channel.CreateUnbounded<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) =>
        { saved.Writer.TryWrite(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0);
        for (var i = 0; i < 24; i++)
        {
            frame.FrameStamp = source.Next();
            Assert.True(scope.TryCapture(frame, "request-" + i, "terminal", "debug"), $"第{i + 1}张因累计配额被拒绝");
            var written = await saved.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(i + 1, written.Sequence);
        }
    }

    [Fact]
    public async Task ARecoveredEpisodeCannotRetainNormalFramesOrSupplyATerminalImage()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        var request = source.Next();
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        var logger = new EvidenceLogger();
        scope.RequestFrame("battle", "old-episode", "search-start", request, "", logger);
        scope.CaptureRequestedFrames("battle", frame);
        scope.EndFrameRequests("battle", "old-episode");
        scope.CaptureRequestedFrames("battle", frame);
        scope.RequestFrame("battle", "next-episode", "terminal", frame.FrameStamp, "", logger);
        await scope.DisposeAsync();
        Assert.DoesNotContain(saved, item => item.Phase == "terminal");
        Assert.Contains(logger.Messages, text => text.Contains("no-next-frame-before-run-end"));
    }

    [Fact]
    public async Task PendingRequestsAreBoundedAndForeignFramesCannotSatisfyThem()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var requested = new CaptureFrameSource().Next();
        var logger = new EvidenceLogger();
        for (var i = 0; i < 4; i++) Assert.True(scope.RequestFrame("battle", "request-" + i, "terminal", requested, "", logger));
        Assert.False(scope.RequestFrame("battle", "overflow", "terminal", requested, "", logger));
        using var foreign = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        scope.CaptureRequestedFrames("battle", foreign);
        await scope.DisposeAsync();
        Assert.Empty(saved);
        Assert.Contains(logger.Messages, text => text.Contains("request-queue-full"));
        Assert.Equal(4, logger.Messages.Count(text => text.StartsWith("EVIDENCE_CAPTURE_MISSING ") && text.Contains("capture-source-changed")));
    }

    [Fact]
    public async Task ARunEndingWithoutAnyFrameRecordsWhyTheTerminalImageIsMissing()
    {
        var logger = new EvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope((_, _) => throw new InvalidOperationException("no frame should reach writer"));
        scope.RequestFrame("battle", "episode", "terminal", new CaptureFrameSource().Next(), "cancelled", logger);
        await scope.DisposeAsync();
        Assert.Contains(logger.Messages, text => text.Contains("no-next-frame-before-run-end"));
    }

    private sealed class EvidenceLogger : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }

    [Fact]
    public async Task TerminalUsesTheLastExistingSearchFrameWhenTheProducerHasAlreadyStopped()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        var requested = source.Next();
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = source.Next() };
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        scope.RequestFrame("battle", "episode", "search-start", requested, "search", logger);
        scope.CaptureRequestedFrames("battle", frame);
        scope.RequestFrame("battle", "episode", "terminal", frame.FrameStamp, "stopped", logger);
        await scope.DisposeAsync(); // 不再提供帧，模拟宿主终态立即取消生产者。
        var terminal = Assert.Single(saved.Where(item => item.Phase == "terminal"));
        Assert.Equal(frame.FrameStamp, terminal.Source);
        Assert.Contains("latest-existing-frame", terminal.Detail);
    }

    [Fact]
    public async Task RequestedHostEvidenceUsesTheNextExistingFrameWithoutRelabellingItsSource()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        var requested = source.Next();
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = source.Next() };
        Assert.True(scope.RequestFrame("battle", "episode", "terminal", requested, "decision",
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        Assert.Empty(saved);
        scope.CaptureRequestedFrames("different-battle", frame);
        scope.CaptureRequestedFrames("battle", frame);
        await scope.DisposeAsync();
        var captured = Assert.Single(saved);
        Assert.Equal(frame.FrameStamp, captured.Source);
        Assert.NotEqual(requested, captured.Source);
        Assert.Contains("requestedSource=", captured.Detail);
    }

    [Fact]
    public async Task PendingAndMaintenanceKeepTheirOwnBeforeFramesWithinTheSameBoundedSlots()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        using var pending = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = source.Next() };
        using var maintenance = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.White), 0, 0)
        { FrameStamp = source.Next() };
        scope.RememberBefore(pending, "skill", "pending-a", "A input before maintenance");
        scope.RememberBefore(maintenance, "skill", "maintenance-b", "B input after A yielded");
        scope.RememberBefore(maintenance, "skill", "overflow-c", "cannot evict A or B");
        scope.ForgetBefore("skill", "maintenance-b"); // B确认不能删除A的暂存。
        scope.CaptureFault(maintenance, "skill", "pending-a", "unconfirmed", "A resumes");
        await scope.DisposeAsync();
        Assert.Contains(saved, item => item.Request == "pending-a" && item.Phase == "before-skill" && item.Source == pending.FrameStamp);
        Assert.DoesNotContain(saved, item => item.Request is "maintenance-b" or "overflow-c");
    }

    [Fact]
    public async Task FaultCannotRelabelAnotherSkillRequestsBeforeFrame()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        using var other = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = source.Next() };
        using var current = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.White), 0, 0)
        { FrameStamp = source.Next() };
        scope.RememberBefore(other, "skill", "request-b", "maintenance B admitted");
        scope.CaptureFault(current, "skill", "request-a", "unconfirmed", "pending A");
        scope.CaptureFault(current, "skill", "request-b", "unconfirmed", "pending B");
        await scope.DisposeAsync();
        Assert.DoesNotContain(saved, item => item.Request == "request-a" && item.Source == other.FrameStamp);
        Assert.Contains(saved, item => item.Request == "request-b" && item.Source == other.FrameStamp);
    }

    [Fact]
    public async Task DuplicatePhasesAndBudgetsAreBoundedAndWriterFailuresDoNotEscape()
    {
        var images = new List<Mat>();
        await using var scope = new DiagnosticEvidenceScope((_, image) =>
        {
            images.Add(image);
            throw new IOException("disk unavailable");
        }, maxImages: 2, maximumBytes: 900);
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        Assert.True(scope.TryCapture(frame, "one", "first", ""));
        Assert.False(scope.TryCapture(frame, "one", "first", ""));
        Assert.True(scope.TryCapture(frame, "one", "second", ""));
        Assert.False(scope.TryCapture(frame, "other", "third", ""));
        await scope.DisposeAsync();
        Assert.Equal(2, images.Count);
        Assert.All(images, image => Assert.True(image.IsDisposed));
    }

    [Fact]
    public async Task DifferentPhasesAreNotLimitedAndUnusedBeforeFramesAreNotPersisted()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        Assert.Null(DiagnosticEvidenceScope.CreateOwned());
        scope.RememberBefore(frame, "skill", "completed", "not a failure");
        scope.ForgetBefore("skill", "completed");
        for (var index = 0; index < 3; index++) Assert.True(scope.TryCapture(frame, "failed", index.ToString(), ""));
        Assert.True(scope.TryCapture(frame, "failed", "fourth", ""));
        Assert.False(scope.TryCapture(frame, "failed", "fourth", "duplicate event"));
        await scope.DisposeAsync();
        Assert.Equal(4, saved.Count);
        Assert.DoesNotContain(saved, item => item.Request == "completed");
    }

    [Fact]
    public async Task OneRequestMayCaptureMoreThanThreeRequestedPhases()
    {
        var saved = System.Threading.Channels.Channel.CreateUnbounded<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) =>
        { saved.Writer.TryWrite(item); return Task.CompletedTask; });
        var source = new CaptureFrameSource();
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0);
        var logger = new EvidenceLogger();
        for (var i = 0; i < 6; i++)
        {
            Assert.True(scope.RequestFrame("battle", "same-request", "phase-" + i, source.Next(), "", logger));
            frame.FrameStamp = source.Next();
            scope.CaptureRequestedFrames("battle", frame);
            var written = await saved.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("phase-" + i, written.Phase);
        }
    }

    [Fact]
    public async Task EvidenceUsesOwnedPixelsAndOriginalSourceAndDrainsBeforeRetirement()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        DiagnosticEvidence? saved = null;
        Mat? owned = null;
        var pixel = default(Vec3b);
        await using var scope = new DiagnosticEvidenceScope(async (evidence, image) =>
        {
            Assert.Null(DiagnosticEvidenceScope.Current);
            saved = evidence;
            owned = image;
            entered.TrySetResult();
            await release.Task;
            pixel = image.At<Vec3b>(0, 0);
        });
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, new Scalar(1, 2, 3)), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        try
        {
            Assert.True(scope.TryCapture(frame, "skill-attempt", "before-input", "not a success claim"));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            frame.SrcMat.Set(0, 0, new Vec3b(9, 9, 9));
            var drain = scope.DisposeAsync().AsTask();
            Assert.False(drain.IsCompleted);
            Assert.False(scope.TryCapture(frame, "late", "after", "rejected"));
            release.SetResult();
            await drain.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(frame.FrameStamp, saved!.Source);
            Assert.Equal(new Vec3b(1, 2, 3), pixel);
            Assert.True(owned!.IsDisposed);
            Assert.False(frame.SrcMat.IsDisposed);
        }
        finally { release.TrySetResult(); }
    }
}
