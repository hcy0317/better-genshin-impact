using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Fischless.WindowsInput;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class SetTimeFlowTests
{
    [Fact]
    public async Task ExpiredParentBudgetDoesNotCreateAnIndependentRecoveryAllowance()
    {
        var replay = new Replay();
        replay.OnDial = () => replay.Clock.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<TimeoutException>(() => UiOperation.RunAsync("parent", TimeSpan.FromSeconds(5), default,
            op => SetTimeFlow.ExecuteAsync(12, 0, false, replay.Io, op.Token), clock: replay.Clock));
        Assert.Equal(1, replay.Closes);
        Assert.Equal(0, replay.Confirms);
    }

    [Fact]
    public async Task NativeMouseFailureAndCleanupFailureAreBothPreserved()
    {
        var original = new IOException("native down unknown");
        var cleanup = new IOException("native up unknown");
        var stage = "down";
        var stages = new List<string>();
        var dispatcher = new WindowsInputMessageDispatcher(null, _ =>
        { stages.Add(stage); throw stage == "down" ? original : cleanup; }, () => 0);
        var error = await Assert.ThrowsAsync<AggregateException>(() => SetTimeTask.HoldMouseAsync(() => Task.CompletedTask,
            () => dispatcher.DispatchInput(new User32.INPUT[1]),
            () => { stage = "up"; dispatcher.DispatchInput(new User32.INPUT[1]); }, default));
        Assert.Equal(new[] { "down", "up" }, stages);
        Assert.Equal(new Exception[] { original, cleanup }, error.InnerExceptions);
    }

    [Fact]
    public async Task MouseCancellationAfterDownStillReleasesWithoutStartingMoreWork()
    {
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        var dispatcher = new WindowsInputMessageDispatcher(null, inputs => { calls++; return (uint)inputs.Length; }, () => 0);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SetTimeTask.HoldMouseAsync(
            () => { cancellation.Cancel(); cancellation.Token.ThrowIfCancellationRequested(); return Task.CompletedTask; },
            () => dispatcher.DispatchInput(new User32.INPUT[1]), () => dispatcher.DispatchInput(new User32.INPUT[1]), cancellation.Token));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResultLoggingCannotTurnConfirmedSuccessIntoTimeoutOrRecovery(bool throwing)
    {
        var logger = new ResultLogger { Throwing = throwing };
        var replay = new Replay(logger);
        logger.Clock = replay.Clock;
        var result = await SetTimeFlow.ExecuteAsync(0, 0, false, replay.Io, default);
        Assert.Equal(SetTimeResult.SatisfiedExistingNearTarget, result);
        Assert.Equal(2, replay.Closes);
    }

    private sealed class ResultLogger : ILogger
    {
        internal FakeTimeProvider Clock = null!;
        internal bool Throwing;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> formatter)
        {
            if (!formatter(state, error).Contains("SET_TIME_RESULT", StringComparison.Ordinal)) return;
            if (Throwing) throw new IOException("log failed");
            Clock.Advance(TimeSpan.FromSeconds(60));
        }
    }

    [Theory]
    [InlineData(718, false, true)]
    [InlineData(717, true, false)]
    [InlineData(691, true, false)]
    [InlineData(690, false, false)]
    public async Task ThirtyMinuteDisableRuleIsNotASuccessTolerance(int current, bool fails, bool existing)
    {
        var replay = new Replay { Current = current, Selected = current };
        if (fails)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => SetTimeFlow.ExecuteAsync(12, 0, true, replay.Io, default));
            Assert.Equal(2, replay.Dials);
            Assert.Equal(0, replay.Confirms);
        }
        else
        {
            var result = await SetTimeFlow.ExecuteAsync(12, 0, true, replay.Io, default);
            Assert.Equal(existing ? SetTimeResult.SatisfiedExistingNearTarget : SetTimeResult.Adjusted, result);
            Assert.Equal(existing ? 0 : 1, replay.Confirms);
            Assert.Equal(existing ? 0 : 1, replay.Skips);
        }
        Assert.Equal(2, replay.Closes);
        Assert.False(replay.Visible);
    }

    [Fact]
    public async Task MidnightAndPublicEntryPreserveTheSameNearTargetContract()
    {
        var replay = new Replay { Current = 1439, Selected = 1439 };
        await new SetTimeTask(replay.Io).Start(24, 0, default, true);
        Assert.Equal(0, replay.Confirms);
        Assert.Equal(2, replay.Closes);
    }

    [Fact]
    public async Task FailedDialRecoversAndPublicStartDoesNotSwallowFailure()
    {
        var replay = new Replay { DialStuck = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SetTimeTask(replay.Io).Start(12, 0, default, true));
        Assert.Equal(2, replay.Dials);
        Assert.Equal(0, replay.Confirms);
        Assert.Equal(0, replay.Skips);
        Assert.Equal(2, replay.Closes);
    }

    [Fact]
    public async Task CancellationDoesNotTriggerNewRecoveryInput()
    {
        using var cancellation = new CancellationTokenSource();
        var replay = new Replay { OnDial = () => cancellation.Cancel() };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SetTimeTask(replay.Io).Start(12, 0, cancellation.Token));
        Assert.Equal(1, replay.Closes);
        Assert.Equal(0, replay.Confirms);
    }

    [Fact]
    public async Task UnconfirmedChangeTimesOutOnceAndReturnsToMainWithoutRepeatingConfirm()
    {
        var replay = new Replay { ApplyConfirmation = false };
        await Assert.ThrowsAsync<TimeoutException>(() => SetTimeFlow.ExecuteAsync(12, 0, true, replay.Io, default));
        Assert.Equal(1, replay.Confirms);
        Assert.Equal(0, replay.Skips);
        Assert.Equal(2, replay.Closes);
        Assert.InRange((replay.Clock.GetUtcNow() - replay.Started).TotalSeconds, 40, 45);
    }

    [Fact]
    public async Task TransportDelayCannotConfirmExpiredSelectedTime()
    {
        var replay = new Replay();
        replay.BeforeConfirm = () => replay.Clock.Advance(TimeSpan.FromSeconds(3));
        await Assert.ThrowsAsync<InvalidOperationException>(() => SetTimeFlow.ExecuteAsync(12, 0, false, replay.Io, default));
        Assert.Equal(0, replay.Confirms);
        Assert.Equal(2, replay.Closes);
    }

    private sealed class Replay
    {
        internal readonly FakeTimeProvider Clock = new();
        internal DateTimeOffset Started => new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        private readonly CaptureFrameSource _source;
        internal int Current, Selected, Closes, Dials, Confirms, Skips;
        internal bool Visible, DialStuck, ApplyConfirmation = true;
        internal Action? OnDial, BeforeConfirm;
        internal SetTimeFlowIo Io { get; }
        internal Replay(ILogger? logger = null)
        {
            _source = new(Clock);
            Io = new()
            {
                Clock = Clock, Logger = logger ?? NullLogger.Instance,
                ReturnMain = ct => { ct.ThrowIfCancellationRequested(); Closes++; Visible = false; return Task.CompletedTask; },
                Open = _ => { Visible = true; return Task.CompletedTask; },
                Observe = () => new(Visible, Current, Selected, SetTimeFlow.Distance(Current, Selected) < 30, _source.Next()),
                SetDial = (h, m, _) => { Dials++; if (!DialStuck) Selected = h * 60 + m; OnDial?.Invoke(); return Task.CompletedTask; },
                Confirm = (admit, _) => { BeforeConfirm?.Invoke(); admit(); Confirms++; if (ApplyConfirmation) Current = Selected; return Task.CompletedTask; },
                SkipAnimation = _ => { Assert.Equal(Selected, Current); Skips++; return Task.CompletedTask; },
                Delay = (ms, ct) => { ct.ThrowIfCancellationRequested(); Clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }
            };
        }
    }

    [Fact]
    public async Task Recorded1201For1200ClosesDisabledClockWithoutConfirmOrSkipAnimation()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        var closes = 0;
        var io = new SetTimeFlowIo
        {
            Clock = clock, Logger = NullLogger.Instance,
            ReturnMain = _ => { closes++; return Task.CompletedTask; },
            Open = _ => Task.CompletedTask,
            Observe = () => new(true, 721, 721, true, producer.Next()),
            SetDial = (_, _, _) => throw new Exception("已近目标不再拨盘"),
            Confirm = (_, _) => throw new Exception("disabled不能确认"),
            SkipAnimation = _ => throw new Exception("未调整不能跳动画"),
            Delay = (ms, ct) => { ct.ThrowIfCancellationRequested(); clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }
        };
        var result = await SetTimeFlow.ExecuteAsync(12, 0, true, io, default);
        Assert.Equal(SetTimeResult.SatisfiedExistingNearTarget, result);
        Assert.Equal(2, closes);
    }
}
