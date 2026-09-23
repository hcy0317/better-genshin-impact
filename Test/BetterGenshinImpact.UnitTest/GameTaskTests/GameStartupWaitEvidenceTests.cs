using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Service;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class GameStartupWaitEvidenceTests
{
    [Fact]
    public async Task StandaloneEvidenceRetiresOnlyItsOwnAmbientScope()
    {
        Assert.Null(DiagnosticEvidenceScope.Current);
        var evidence = GameStartupWaitEvidence.Begin(NullLogger.Instance);
        Assert.NotNull(evidence);
        Assert.NotNull(DiagnosticEvidenceScope.Current);
        var drain = evidence.DisposeAsync();
        Assert.Null(DiagnosticEvidenceScope.Current);
        await drain;
        await evidence.DisposeAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingCacheOrDiagnosticLoggerFailureNeverReplacesOriginalTimeout(bool failClone)
    {
        await using var scope = new DiagnosticEvidenceScope((_, _) => throw new IOException("disk failed"));
        await using var evidence = GameStartupWaitEvidence.Begin(new ThrowingLogger());
        Assert.NotNull(evidence);
        var calls = 0;
        (ImageRegion?, TimeSpan) Clone()
        {
            calls++;
            if (failClone) throw new IOException("cache unavailable");
            return (null, TimeSpan.Zero);
        }
        var original = new TimeoutException("original startup deadline");
        void Timeout()
        {
            evidence.CaptureTimeout(Clone, TimeSpan.FromMinutes(5), "unknown");
            evidence.CaptureTimeout(Clone, TimeSpan.FromMinutes(5), "unknown");
            throw original;
        }
        Assert.Same(original, Assert.Throws<TimeoutException>(Timeout));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RejectedUnknownSourceIsNotRetriedOnEveryWaitingLoop()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((record, _) => { saved.Add(record); return Task.CompletedTask; });
        await using var evidence = GameStartupWaitEvidence.Begin(NullLogger.Instance);
        using var frame = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0);
        evidence!.Observe(frame, false, TimeSpan.FromSeconds(30), TimeSpan.Zero, "unknown");
        frame.FrameStamp = new CaptureFrameSource().Next();
        evidence.Observe(frame, false, TimeSpan.FromSeconds(31), TimeSpan.Zero, "unknown");
        await scope.DisposeAsync();
        Assert.Empty(saved);
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => throw new IOException("logger failed");
    }

    [Fact]
    public async Task ExistingScopeReceivesOnlyWaitingAndTerminalWithOriginalCacheStamp()
    {
        var saved = new List<DiagnosticEvidence>();
        await using var scope = new DiagnosticEvidenceScope((record, _) => { saved.Add(record); return Task.CompletedTask; });
        using var cache = new FailureScreenshotFrameCache(TimeSpan.FromSeconds(1));
        using var source = new ImageRegion(new Mat(2, 2, MatType.CV_8UC3, Scalar.Black), 0, 0)
        { FrameStamp = new CaptureFrameSource().Next() };
        var accepted = DateTimeOffset.UtcNow;
        cache.TryUpdate(source.SrcMat, accepted, source.FrameStamp);
        await using var evidence = GameStartupWaitEvidence.Begin(NullLogger.Instance);
        Assert.NotNull(evidence);
        evidence.Observe(source, false, TimeSpan.FromSeconds(29.9), TimeSpan.Zero, "unknown");
        evidence.Observe(source, true, TimeSpan.FromSeconds(30), TimeSpan.Zero, "main-ui");
        evidence.Observe(source, false, TimeSpan.FromSeconds(30), TimeSpan.Zero, "unknown");
        evidence.Observe(source, false, TimeSpan.FromSeconds(31), TimeSpan.Zero, "unknown");
        ImageRegion? terminal = null;
        var calls = 0;
        (ImageRegion?, TimeSpan) Clone()
        {
            calls++;
            var pixels = cache.TryClone(accepted.AddSeconds(4), out var age, out var stamp);
            terminal = new ImageRegion(pixels!, 0, 0) { FrameStamp = stamp };
            return (terminal, age);
        }
        evidence.CaptureTimeout(Clone, TimeSpan.FromMinutes(5), "unknown");
        evidence.CaptureTimeout(Clone, TimeSpan.FromMinutes(5), "unknown");
        await evidence.DisposeAsync();
        Assert.Same(scope, DiagnosticEvidenceScope.Current);
        Assert.False(source.SrcMat.IsDisposed);
        Assert.True(terminal!.SrcMat.IsDisposed);
        await scope.DisposeAsync();
        Assert.Equal(1, calls);
        Assert.Equal(new[] { "waiting", "timeout" }, saved.Select(record => record.Phase));
        Assert.All(saved, record => Assert.Equal(source.FrameStamp, record.Source));
        Assert.Contains("cacheAgeSeconds=4.000", saved[1].Detail);
    }
}
