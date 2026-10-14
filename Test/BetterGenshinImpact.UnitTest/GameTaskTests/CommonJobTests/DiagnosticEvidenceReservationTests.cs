using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class DiagnosticEvidenceReservationTests
{
    [Theory]
    [InlineData("closed")]
    [InlineData("invalid-key")]
    [InlineData("source-unknown")]
    [InlineData("clock-mismatch")]
    [InlineData("deadline")]
    [InlineData("cancellation")]
    public async Task InitialReservationRejectionReturnsItsSpecificReason(string failure)
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        var key = new DiagnosticFrameReservationKey(Guid.NewGuid(), Guid.NewGuid(), 1);
        var stamp = source.Next();
        if (failure == "closed") await scope.DisposeAsync();
        if (failure == "invalid-key") key = key with { Request = Guid.Empty };
        if (failure == "source-unknown") stamp = default;
        if (failure == "clock-mismatch") stamp = stamp with { TimestampFrequency = clock.TimestampFrequency + 1 };
        using var token = scope.TryReserveExactFrame(key, stamp, clock,
            clock.GetTimestamp() + (failure == "deadline" ? 0 : clock.TimestampFrequency),
            new CancellationToken(failure == "cancellation"), out var reason);
        Assert.Null(token);
        Assert.Equal("evidence-unavailable:" + failure, reason);
        Assert.Equal(0, scope.ExactReservationStatistics.Active);
    }

    [Fact]
    public async Task CloseAfterFulfillBeforeClaimRejectsClaimAndReturnsPixelsExactlyOnce()
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var token = scope.TryReserveExactFrame(new(Guid.NewGuid(), Guid.NewGuid(), 1), source.Next(), clock,
            clock.GetTimestamp() + clock.TimestampFrequency, default);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        Assert.True(scope.TryFulfillExactFrame(token!, frame));
        await scope.DisposeAsync();
        Assert.Null(scope.TryAcquireExactFrame(token!));
        token!.Dispose(); token.Dispose();
        Assert.Equal((0, 1L, 1L), scope.ExactReservationStatistics);
        Assert.Equal("evidence-unavailable:closed", token.FailureReason);
    }

    [Fact]
    public async Task CancellationAfterClaimKeepsOwnershipUntilSavedAndFinallyReleased()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((item, _) => { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var cancellation = new CancellationTokenSource();
        var key = new DiagnosticFrameReservationKey(Guid.NewGuid(), Guid.NewGuid(), 1);
        using var token = scope.TryReserveExactFrame(key, source.Next(), clock, clock.GetTimestamp() + clock.TimestampFrequency, cancellation.Token);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        Assert.True(scope.TryFulfillExactFrame(token!, frame));
        using var lease = scope.TryAcquireExactFrame(token!);
        cancellation.Cancel();
        Assert.Equal(DiagnosticFrameReservationState.Claimed, token!.State);
        Assert.Null(token.FailureReason);
        Assert.True(scope.SaveExactFrame(lease!, key, lease!.Source, "terminal", "original cancellation"));
        lease.Dispose(); lease.Dispose();
        await scope.DisposeAsync();
        Assert.Single(saved);
        Assert.Equal(DiagnosticFrameReservationState.Released, token.State);
        Assert.Equal((0, 1L, 1L), scope.ExactReservationStatistics);
    }

    [Theory]
    [InlineData("terminal-image-budget")]
    [InlineData("terminal-queue-full")]
    [InlineData("terminal-identity-mismatch")]
    public async Task RejectedTerminalSaveAccountsOneStableMissingReason(string reason)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logger = new MissingEvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope(async (_, _) => { entered.TrySetResult(); await release.Task; },
            maxImages: reason == "terminal-image-budget" ? 1 : 8, queueCapacity: 1);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        Assert.True(scope.TryCapture(frame, "writer", "terminal", "", logger, priority: DiagnosticEvidencePriority.Error));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var key = new DiagnosticFrameReservationKey(Guid.NewGuid(), Guid.NewGuid(), 1);
            using var token = scope.TryReserveExactFrame(key, frame.FrameStamp, clock, clock.GetTimestamp() + clock.TimestampFrequency, default);
            frame.FrameStamp = source.Next();
            Assert.True(scope.TryFulfillExactFrame(token!, frame));
            using var lease = scope.TryAcquireExactFrame(token!);
            if (reason == "terminal-queue-full")
                Assert.True(scope.TryCapture(frame, "queued", "terminal", "", priority: DiagnosticEvidencePriority.Error));
            var saveKey = reason == "terminal-identity-mismatch" ? key with { Request = Guid.NewGuid() } : key;
            Assert.False(scope.SaveExactFrame(lease!, saveKey, lease!.Source, "terminal", "original"));
            Assert.False(scope.SaveExactFrame(lease, saveKey, lease.Source, "terminal", "duplicate"));
            Assert.Equal("evidence-unavailable:" + reason, token!.FailureReason);
            lease.Dispose();
            release.TrySetResult();
            await scope.DisposeAsync();
            Assert.Single(logger.Messages.Where(message => message.StartsWith("EVIDENCE_CAPTURE_MISSING ") && message.Contains(reason)));
            Assert.Contains("\"capture:evidence-unavailable:" + reason + "\":1",
                Assert.Single(logger.Messages.Where(message => message.StartsWith("EVIDENCE_DRAINED "))));
        }
        finally { release.TrySetResult(); }
    }

    private sealed class MissingEvidenceLogger : Microsoft.Extensions.Logging.ILogger
    {
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Messages = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Enqueue(formatter(state, exception));
    }

    [Fact]
    public async Task CloseBeforeTerminalSaveReportsMissingOnceWithoutReleasingTheLiveLease()
    {
        var logger = new MissingEvidenceLogger();
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        var key = new DiagnosticFrameReservationKey(Guid.NewGuid(), Guid.NewGuid(), 1);
        using var token = scope.TryReserveExactFrame(key, source.Next(), clock,
            clock.GetTimestamp() + clock.TimestampFrequency, default);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        Assert.True(scope.TryCapture(frame, "warmup", "terminal", "", logger));
        Assert.True(scope.TryFulfillExactFrame(token!, frame));
        using var lease = scope.TryAcquireExactFrame(token!);
        await scope.DisposeAsync();
        Assert.Equal("evidence-unavailable:closed-before-terminal-save", token!.FailureReason);
        Assert.False(scope.SaveExactFrame(lease!, key, lease!.Source, "terminal", "original failure"));
        Assert.False(scope.SaveExactFrame(lease, key, lease.Source, "terminal", "repeat"));
        Assert.Single(logger.Messages.Where(message => message.StartsWith("EVIDENCE_CAPTURE_MISSING ") && message.Contains("closed-before-terminal-save")));
        Assert.Contains("\"capture:evidence-unavailable:closed-before-terminal-save\":1",
            Assert.Single(logger.Messages.Where(message => message.StartsWith("EVIDENCE_DRAINED "))));
        Assert.Equal(0, scope.ExactReservationStatistics.Released);
        lease.Dispose();
        Assert.Equal(1, scope.ExactReservationStatistics.Released);
    }

    [Fact]
    public async Task CancelledReservationHasOneTerminalReasonAndImmediatelyFreesItsSlot()
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        using var cancellation = new CancellationTokenSource();
        using var token = scope.TryReserveExactFrame(new(Guid.NewGuid(), Guid.NewGuid(), 1), source.Next(), clock,
            clock.GetTimestamp() + clock.TimestampFrequency, cancellation.Token);
        cancellation.Cancel();
        Assert.Equal(DiagnosticFrameReservationState.Cancelled, token!.State);
        Assert.Equal("evidence-unavailable:cancellation", token.FailureReason);
        token.Dispose(); token.Dispose();
        Assert.Equal(0, scope.ExactReservationStatistics.Active);
        Assert.Equal(0, scope.ExactReservationStatistics.Retained);
    }

    [Fact]
    public async Task WriterBorrowDoesNotChargeTheSamePixelsTwiceAgainstTheByteLimit()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var scope = new DiagnosticEvidenceScope(async (_, _) => { entered.TrySetResult(); await release.Task; }, maximumBytes: 40);
        var clock = new FakeTimeProvider(); var source = new CaptureFrameSource(clock);
        var key = new DiagnosticFrameReservationKey(Guid.NewGuid(), Guid.NewGuid(), 1);
        using var token = scope.TryReserveExactFrame(key, source.Next(), clock, clock.GetTimestamp() + clock.TimestampFrequency, default);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        Assert.True(scope.TryFulfillExactFrame(token!, frame));
        using var lease = scope.TryAcquireExactFrame(token!);
        Assert.True(scope.SaveExactFrame(lease!, key, lease!.Source, "terminal", ""));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var second = scope.TryReserveExactFrame(key with { Request = Guid.NewGuid() }, frame.FrameStamp, clock,
                clock.GetTimestamp() + clock.TimestampFrequency, default);
            frame.FrameStamp = source.Next();
            Assert.True(scope.TryFulfillExactFrame(second!, frame));
        }
        finally { release.TrySetResult(); }
    }
    [Fact]
    public async Task ClosingScopeKeepsAnAlreadyAdmittedWriterAliveButRejectsNewClaims()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte pixel = 0;
        await using var scope = new DiagnosticEvidenceScope(async (_, image) =>
        {
            entered.TrySetResult();
            await release.Task;
            pixel = image.At<Vec3b>(0, 0).Item0;
        });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var key = new DiagnosticFrameReservationKey(Guid.NewGuid(), Guid.NewGuid(), 1);
        using var token = scope.TryReserveExactFrame(key, source.Next(), clock,
            clock.GetTimestamp() + clock.TimestampFrequency, CancellationToken.None);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(19, 0, 0)), 0, 0) { FrameStamp = source.Next() };
        Assert.True(scope.TryFulfillExactFrame(token!, frame));
        using var lease = scope.TryAcquireExactFrame(token!);
        Assert.True(scope.SaveExactFrame(lease!, key, lease!.Source, "terminal", ""));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var closing = scope.DisposeAsync().AsTask();
            Assert.Null(scope.TryAcquireExactFrame(token!));
            Assert.False(scope.SaveExactFrame(lease, key, lease.Source, "again", ""));
            lease.Dispose();
            lease.Dispose();
            release.TrySetResult();
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(19, pixel);
            Assert.Null(token!.FailureReason);
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [InlineData(100, 0, false)]
    [InlineData(1000, 151, false)]
    [InlineData(1000, 50, true)]
    public async Task ProducerWaitDoesNotResetDeadlineOrPermitStalePixels(int remainingMs, int recognitionMs, bool accepted)
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var key = new DiagnosticFrameReservationKey(Guid.NewGuid(), Guid.NewGuid(), 1);
        using var token = scope.TryReserveExactFrame(key, source.Next(), clock,
            clock.GetTimestamp() + remainingMs * clock.TimestampFrequency / 1000, CancellationToken.None);
        Assert.NotNull(token);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        clock.Advance(TimeSpan.FromMilliseconds(recognitionMs));
        Assert.Equal(accepted, scope.TryFulfillExactFrame(token!, frame));
        using var lease = scope.TryAcquireExactFrame(token!);
        Assert.Equal(accepted, lease != null);
    }

    [Fact]
    public async Task ReservationCannotEvictPartyFramesOrFulfillTwice()
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0) { FrameStamp = source.Next() };
        var key = new DiagnosticFrameReservationKey(Guid.NewGuid(), Guid.NewGuid(), 1);
        scope.RememberExactFrame("party", "one", frame);
        scope.RememberExactFrame("party-admitted", "two", frame);
        var sourceStamp = frame.FrameStamp;
        Assert.Null(scope.TryReserveExactFrame(key, frame.FrameStamp, clock,
            clock.GetTimestamp() + clock.TimestampFrequency, CancellationToken.None));
        scope.ForgetExactFrame("party", "one");
        using var token = scope.TryReserveExactFrame(key, frame.FrameStamp, clock,
            clock.GetTimestamp() + clock.TimestampFrequency, CancellationToken.None);
        Assert.NotNull(token);
        frame.FrameStamp = source.Next();
        Assert.True(scope.TryFulfillExactFrame(token!, frame));
        for (var i = 0; i < 100; i++) Assert.False(scope.TryFulfillExactFrame(token!, frame));
        using var lease = scope.TryAcquireExactFrame(token!);
        Assert.NotNull(lease);
        Assert.Null(scope.TryAcquireExactFrame(token!));
        Assert.False(scope.SaveExactFrame(lease!, key with { Request = Guid.NewGuid() }, lease!.Source, "terminal", "wrong request"));
        lease.Dispose();
        lease.Dispose();
        Assert.True(scope.CaptureExactWindow("party-admitted", "two", sourceStamp, "terminal", "unaffected"));
    }

    [Fact]
    public async Task ReservedDecisionSurvivesProducerDisposalAndKeepsItsExactPixels()
    {
        var saved = new List<(CaptureFrameStamp Stamp, byte Pixel)>();
        await using var scope = new DiagnosticEvidenceScope((evidence, image) =>
        { saved.Add((evidence.Source, image.At<Vec3b>(0, 0).Item0)); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var before = source.Next();
        var key = new DiagnosticFrameReservationKey(Guid.NewGuid(), Guid.NewGuid(), 1);
        using var reservation = scope.TryReserveExactFrame(key, before, clock,
            clock.GetTimestamp() + clock.TimestampFrequency, CancellationToken.None);
        Assert.NotNull(reservation);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        CaptureFrameStamp decision;
        using (var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, new Scalar(7, 0, 0)), 0, 0)
               { FrameStamp = source.Next() })
        {
            decision = frame.FrameStamp;
            clock.Advance(TimeSpan.FromMilliseconds(50));
            Assert.True(scope.TryFulfillExactFrame(reservation!, frame));
        }
        using var lease = scope.TryAcquireExactFrame(reservation!);
        Assert.NotNull(lease);
        Assert.Equal(decision, lease!.Source);
        Assert.True(scope.SaveExactFrame(lease, key, decision, "terminal", "original decision"));
        lease.Dispose();
        await scope.DisposeAsync();
        Assert.Equal((decision, (byte)7), Assert.Single(saved));
    }
}
