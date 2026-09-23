using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class HandbookNativeEscapeTests
{
    [Theory]
    [InlineData("observed-stale")]
    [InlineData("focus-stale")]
    [InlineData("current-stale")]
    [InlineData("frozen-observed-clock")]
    [InlineData("unbound")]
    [InlineData("unknown-source")]
    [InlineData("foreign-source")]
    [InlineData("repeat")]
    [InlineData("old-fence")]
    [InlineData("world")]
    [InlineData("unknown-page")]
    [InlineData("full-defeat")]
    public async Task EscapeStillRejectsInvalidProposalOrCurrentEvidence(string fault)
    {
        using var fixture = Handbook();
        var observed = fixture.Driver.Capture();
        switch (fault)
        {
            case "observed-stale": fixture.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "focus-stale": fixture.OnFocus = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "current-stale": fixture.AfterCapture = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3)); break;
            case "frozen-observed-clock":
                var frozen = new FakeTimeProvider();
                frozen.Advance(TimeSpan.FromMilliseconds(1));
                observed = observed.WithSource(observed.SourceStamp, frozen, UiSnapshot.RecoveryMaximumAge);
                fixture.Clock.Advance(TimeSpan.FromSeconds(3));
                break;
            case "unbound": observed = new(1) { Handbook = true }; break;
            case "unknown-source": fixture.SourceOverride = _ => default; break;
            case "foreign-source": fixture.SourceOverride = _ => new CaptureFrameSource(fixture.Clock).Next(); break;
            case "repeat": fixture.SourceOverride = _ => observed.SourceStamp; break;
            case "old-fence":
                fixture.Driver.MarkInputCompleted(observed);
                fixture.SourceOverride = _ => fixture.Producer.Next(observed.SourceStamp.CapturedTimestamp);
                break;
            case "world": fixture.OnFocus = () => fixture.Scene = new(1) { MainHud = true }; break;
            case "unknown-page": fixture.OnFocus = () => fixture.Scene = new(1); break;
            case "full-defeat": fixture.OnFocus = () => fixture.Scene = new(1) { FullPartyDefeat = true, Handbook = true }; break;
        }
        Assert.False(await fixture.Driver.ActAsync(UiAction.Escape, observed, default));
        Assert.Empty(fixture.Actions);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }

    [Fact]
    public async Task NativeTransportPreparationCannotUseAnExpiredCurrentFrame()
    {
        using var fixture = Handbook();
        var observed = fixture.Driver.Capture();
        fixture.BeforeActionTransport = () => fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Driver.ActAsync(UiAction.Escape, observed, default));
        Assert.Empty(fixture.Actions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentReadCannotOutliveCancellationOrOriginalUiDeadline(bool cancel)
    {
        using var fixture = Handbook();
        using var cancellation = new CancellationTokenSource();
        var observed = fixture.Driver.Capture();
        fixture.AfterCapture = () =>
        {
            if (cancel) cancellation.Cancel();
            else fixture.Clock.Advance(TimeSpan.FromSeconds(21));
        };
        var failure = await Record.ExceptionAsync(() => UiOperation.RunAsync("handbook-deadline", TimeSpan.FromSeconds(20),
            cancellation.Token, op => fixture.Driver.ActAsync(UiAction.Escape, observed, op.Token), clock: fixture.Clock));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsType<TimeoutException>(failure);
        Assert.Empty(fixture.Actions);
    }

    [Fact]
    public async Task SlowPreInputLogCannotAuthorizeAnExpiredCurrentFrame()
    {
        using var fixture = Handbook();
        var observed = fixture.Driver.Capture();
        fixture.OnFocus = () => fixture.Scene = fixture.Scene with { InDomain = true };
        var logger = new SlowLog(fixture.Clock);
        var applied = await UiOperation.RunAsync("handbook-log", TimeSpan.FromSeconds(20), default, op =>
        {
            op.Observe(observed, UiTarget.Main);
            return fixture.Driver.ActAsync(UiAction.Escape, observed, op.Token);
        }, logger, fixture.Clock);
        Assert.True(logger.Delayed);
        Assert.False(applied);
        Assert.Empty(fixture.Actions);
    }

    private static DomainTipNativeFixture Handbook() => new()
    { Scene = new(1) { Handbook = true }, Title = false, Footer = false, OnAction = _ => true };

    private sealed class SlowLog(FakeTimeProvider clock) : ILogger
    {
        internal bool Delayed;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (!Delayed && formatter(state, error).Contains("phase=pre-input", StringComparison.Ordinal))
            { Delayed = true; clock.Advance(TimeSpan.FromSeconds(2.5)); }
        }
    }

    [Fact]
    public async Task Two1220msReadsCanCloseHandbookAndConfirmNewHudBeforeOriginalDeadline()
    {
        using var fixture = new DomainTipNativeFixture
        {
            Scene = new(1) { Handbook = true }, Title = false, Footer = false
        };
        fixture.AfterCapture = () => fixture.Clock.Advance(TimeSpan.FromMilliseconds(1220));
        fixture.OnAction = action => { fixture.Scene = new(1) { MainHud = true }; return true; };
        var started = fixture.Clock.GetUtcNow();
        var result = await UiRecovery.ToMainAsync(fixture.Driver, default, clock: fixture.Clock);
        Assert.True(result.MainReady);
        Assert.Equal(UiAction.Escape, Assert.Single(fixture.Actions));
        Assert.InRange((fixture.Clock.GetUtcNow() - started).TotalSeconds, 4, 7);
        Assert.All(fixture.Frames, frame => Assert.True(frame.SrcMat.IsDisposed));
    }
}
