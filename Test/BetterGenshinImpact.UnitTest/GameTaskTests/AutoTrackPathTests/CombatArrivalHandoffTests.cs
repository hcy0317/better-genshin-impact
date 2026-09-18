using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoTrackPathTests;

public class CombatArrivalHandoffTests
{
    [Theory]
    [InlineData("fight", 0)]
    [InlineData("combat_script", 1000)]
    [InlineData("mining", 1000)]
    [InlineData("", 1000)]
    public async Task ArrivalReleasesMovementAndDoesNotIdleBeforeCombat(string action, int expectedDelay)
    {
        var events = new List<string>();
        var waited = 0;
        await PathExecutor.SettleArrivalAsync(action, () => events.Add("release"), (ms, ct) =>
        {
            Assert.Equal("release", events[0]);
            waited += ms;
            return Task.CompletedTask;
        }, default);
        await PathExecutor.DispatchArrivalActionAsync(new RecordingHandler(events), null, null, default);
        Assert.Equal(new[] { "release", "handler" }, events);
        Assert.Equal(expectedDelay, waited);
    }

    [Fact]
    public async Task CancellationStillReleasesMovementAndNeverDispatchesTheNextAction()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var released = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PathExecutor.SettleArrivalAsync("fight",
            () => released = true, (_, _) => throw new Exception("should not wait"), cancellation.Token));
        Assert.True(released);
        var events = new List<string>();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PathExecutor.DispatchArrivalActionAsync(
            new RecordingHandler(events), null, null, cancellation.Token));
        Assert.Empty(events);
    }

    private sealed class RecordingHandler(List<string> events) : IActionHandler
    {
        public Task RunAsync(CancellationToken ct, WaypointForTrack? waypointForTrack = null, object? config = null)
        {
            ct.ThrowIfCancellationRequested();
            events.Add("handler");
            return Task.CompletedTask;
        }
    }
}
