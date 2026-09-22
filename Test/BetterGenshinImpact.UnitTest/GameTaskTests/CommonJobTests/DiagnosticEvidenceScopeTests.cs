using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DiagnosticEvidenceScopeTests
{
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
        Assert.Contains(logger.Messages, message => message.Contains("imageLimit=unlimited") && message.Contains("byteLimit=unlimited"));
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
        });
        using var frame = new ImageRegion(new Mat(10, 10, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var logger = new EvidenceLogger();
        try
        {
            Assert.True(scope.TryCapture(frame, "active", "first", "", logger));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 4; i++) Assert.True(scope.TryCapture(frame, "queued-" + i, "first", "", logger));
            var rejected = Task.Run(() => scope.TryCapture(frame, "overflow", "first", "", logger));
            Assert.False(await rejected.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Contains(logger.Messages, message => message.Contains("writer-queue-full"));
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
        Assert.Equal(4, logger.Messages.Count(text => text.Contains("capture-source-changed")));
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
        }, maxImages: 2, maximumBytes: 600);
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
