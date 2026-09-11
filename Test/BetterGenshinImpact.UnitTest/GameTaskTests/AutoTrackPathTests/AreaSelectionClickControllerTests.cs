using BetterGenshinImpact.GameTask.AutoTrackPath;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class AreaSelectionClickControllerTests
{




    [Fact]
    public async Task TimeoutReportsCapturedMapStateInsteadOfClaimingNoObservation()
    {
        var clock = new FakeTimeProvider();
        long frame = 0;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => AreaSelectionClickController.TryApplyAsync(
            () => new(++frame, true, false, false),
            (_, _) => throw new InvalidOperationException("no visible candidate"),
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, clock: clock));
        Assert.Contains("mapReady=True", error.Message);
        Assert.Contains("selectorOpen=False", error.Message);
        Assert.Contains("clicked=False", error.Message);
        Assert.DoesNotContain("未取得观察", error.Message);
        Assert.DoesNotContain("operation-complete", error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UnknownOrStillOpenPickerCannotCompleteSelection(bool mapReady, bool selectorOpen)
    {
        var clock = new FakeTimeProvider();
        var inputs = 0;
        long frame = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => AreaSelectionClickController.TryApplyAsync(
            () => ++frame == 1 ? new(frame, true, true, true) : new(frame, mapReady, selectorOpen, false),
            (_, _) => { inputs++; return Task.FromResult(true); },
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, clock: clock));
        Assert.Equal(1, inputs);
    }

    [Fact]
    public async Task ExistingMapWithoutAnAppliedSelectionIsNotSuccess()
    {
        var clock = new FakeTimeProvider();
        var inputs = 0;
        long frame = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => AreaSelectionClickController.TryApplyAsync(
            () => new(++frame, true, false, false),
            (_, _) => { inputs++; return Task.FromResult(true); },
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, clock: clock));
        Assert.Equal(0, inputs);
    }

    [Fact]
    public async Task SelectionCancellationAndParentDeadlineNeverRestartTheInputBudget()
    {
        var clock = new FakeTimeProvider();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var inputs = 0;
        Task<bool> Run(CancellationToken ct) => AreaSelectionClickController.TryApplyAsync(
            () => new(1, true, true, true),
            (_, _) => { inputs++; return Task.FromResult(true); },
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            ct, clock: clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(cancelled.Token));
        Assert.Equal(0, inputs);
        using var parent = UiOperation.Begin("parent", TimeSpan.FromSeconds(1), clock: clock);
        await Assert.ThrowsAsync<TimeoutException>(() => Run(default));
        Assert.Equal(TimeSpan.Zero, parent.Remaining);
        Assert.Equal(1, inputs);
    }

    [Fact]
    public async Task LateMapTransitionIsAcceptedWithoutClickingDisappearedCoordinates()
    {
        var clock = new FakeTimeProvider();
        var inputs = 0;
        long frame = 0;
        var started = clock.GetTimestamp();
        var applied = await AreaSelectionClickController.TryApplyAsync(
            () => new AreaSelectionObservation(++frame, true,
                clock.GetElapsedTime(started) < TimeSpan.FromSeconds(2),
                clock.GetElapsedTime(started) < TimeSpan.FromSeconds(2)),
            (_, _) =>
            {
                Assert.True(clock.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
                inputs++;
                return Task.FromResult(true);
            },
            (ms, _) => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; },
            default, clock: clock);
        Assert.True(applied);
        Assert.InRange(inputs, 1, 2);
    }

    [Fact]
    public async Task TryApplyAsync_RetriesUntilSelectionIsConfirmed()
    {
        var outcomes = new Queue<bool>([false, false, true]);
        var attempts = new List<int>();

        var applied = await AreaSelectionClickController.TryApplyAsync(
            maxAttempts: 3,
            async attempt =>
            {
                attempts.Add(attempt);
                await Task.Yield();
                return outcomes.Dequeue();
            });

        Assert.True(applied);
        Assert.Equal([1, 2, 3], attempts);
    }

    [Fact]
    public async Task TryApplyAsync_StopsAfterBoundedUnconfirmedClicks()
    {
        var attempts = new List<int>();

        var applied = await AreaSelectionClickController.TryApplyAsync(
            maxAttempts: 3,
            attempt =>
            {
                attempts.Add(attempt);
                return Task.FromResult(false);
            });

        Assert.False(applied);
        Assert.Equal([1, 2, 3], attempts);
    }
}
