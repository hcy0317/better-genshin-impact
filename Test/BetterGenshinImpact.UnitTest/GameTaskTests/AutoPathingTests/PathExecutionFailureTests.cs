using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask.Common.Ui;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public class PathExecutionFailureTests
{
    [Fact]
    public async Task UnsafeMacroPrefixStillHealsButCannotRetryOrCompleteTheRoute()
    {
        var replay = new PathReplay();
        var points = Enumerable.Range(0, 4).Select(_ => replay.Point("walk")).ToList();
        points[0].Type = "teleport";
        points[1].Action = "combat_script";
        points[2].Action = points[3].Action = "mining";
        replay.Executor.CurWaypoints = (0, points);
        replay.Executor.CurWaypoint = (3, points[3]);
        replay.Executor.StartSkipOtherOperations();
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var recovered = 0;
        var retries = 0;
        var releases = 0;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => PathExecutor.ConfirmHealingRestartAsync(source.Next(),
                () => { recovered++; clock.Advance(TimeSpan.FromSeconds(1)); return Task.CompletedTask; },
                () => { clock.Advance(TimeSpan.FromMilliseconds(1)); return new(source.Next(), true, false); },
                ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock,
                canRestart: PathExecutor.CanRestartAfterHealing(points, 3)),
            _ => retries++, () => releases++, default));
        Assert.Equal(1, recovered);
        Assert.Equal(0, retries);
        Assert.Equal(1, releases);
        replay.Executor.CurWaypoint = (2, points[2]);
        replay.Executor.TryCloseSkipOtherOperations();
        Assert.False(replay.Executor.ShouldExecuteWaypointAction(points[2]));
        replay.Executor.CurWaypoint = (3, points[3]);
        replay.Executor.TryCloseSkipOtherOperations();
        Assert.True(replay.Executor.ShouldExecuteWaypointAction(points[3]));
        Assert.Contains("路线未完成", error.Message);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ParentDeadlineDuringHealingOrObservationCannotAuthorizeRestart(bool inObservation, bool canRestart)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var before = source.Next();
        using var parent = UiOperation.Begin("business-parent", TimeSpan.FromSeconds(1), clock: clock);
        var retries = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => PathExecutor.ConfirmHealingRestartAsync(before,
                () => { if (!inObservation) clock.Advance(TimeSpan.FromSeconds(2)); return Task.CompletedTask; },
                () => { clock.Advance(inObservation ? TimeSpan.FromMilliseconds(1100) : TimeSpan.FromMilliseconds(1)); return new(source.Next(), true, false); },
                ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock, canRestart),
            _ => retries++, () => { }, default));
        Assert.Equal(0, retries);
    }

    [Theory]
    [InlineData("unknown-source")]
    [InlineData("cancelled")]
    [InlineData("statue-failed")]
    [InlineData("still-low")]
    [InlineData("old-frame")]
    public async Task UnsafeRouteCannotClaimHealingWithoutRecoveryEvidence(string failure)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock);
        var before = failure == "unknown-source" ? default : source.Next();
        using var cancellation = new CancellationTokenSource();
        if (failure == "cancelled") cancellation.Cancel();
        var calls = 0;
        var expected = new InvalidOperationException("statue failed");
        var error = await Record.ExceptionAsync(() => PathExecutor.ConfirmHealingRestartAsync(before,
            () =>
            {
                calls++;
                if (failure == "statue-failed") throw expected;
                clock.Advance(TimeSpan.FromSeconds(1));
                return Task.CompletedTask;
            }, () =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                return new HealingFrame(failure == "old-frame" ? before : source.Next(), true, failure == "still-low");
            }, ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, cancellation.Token, clock,
            canRestart: false));
        Assert.NotNull(error);
        Assert.IsNotType<PathExecutor.HealingRecoveryCompletedException>(error);
        Assert.DoesNotContain("已确认神像回血", error.Message);
        Assert.Equal(failure is "unknown-source" or "cancelled" ? 0 : 1, calls);
        if (failure == "statue-failed") Assert.Same(expected, error);
        if (failure == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(error);
    }

    [Theory]
    [InlineData(true, 3)]
    [InlineData(false, 2)]
    public async Task ExtraRecoveryRestartsHaveIndependentFiniteLimits(bool healing, int expectedAttempts)
    {
        var attempts = 0; var releases = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(() =>
        {
            attempts++;
            if (healing) throw new PathExecutor.HealingRecoveryCompletedException();
            throw new RetryNoCountException("relocate once");
        }, _ => { }, () => releases++, default));
        Assert.Equal(expectedAttempts, attempts);
        Assert.Equal(attempts, releases);
    }

    [Fact]
    public async Task RecordedTerminalCombatCannotBeHiddenByARecoveryException()
    {
        using var scope = TaskExecutionScope.BeginOwned();
        var retries = 0;
        await Assert.ThrowsAsync<CombatNotFinishedException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(() =>
        {
            Assert.Throws<CombatNotFinishedException>(() => TaskExecutionScope.StopUnconfirmedCombat("terminal"));
            throw new PathExecutor.HealingRecoveryCompletedException();
        }, _ => retries++, () => { }, default));
        Assert.Equal(0, retries);
    }

    [Fact]
    public void EarlierRecoveryDuringReplayCannotMoveTheSideEffectCheckpointBackwards()
    {
        var replay = new PathReplay();
        var points = Enumerable.Range(0, 11).Select(_ => replay.Point("walk")).ToList();
        foreach (var point in points) point.Action = "mining";
        replay.Executor.CurWaypoints = (0, points);
        replay.Executor.CurWaypoint = (10, points[10]);
        replay.Executor.StartSkipOtherOperations();
        replay.Executor.CurWaypoint = (3, points[3]);
        replay.Executor.TryCloseSkipOtherOperations();
        replay.Executor.StartSkipOtherOperations();
        for (var index = 4; index < 10; index++)
        {
            replay.Executor.CurWaypoint = (index, points[index]);
            replay.Executor.TryCloseSkipOtherOperations();
            Assert.False(replay.Executor.ShouldExecuteWaypointAction(points[index]));
        }
        replay.Executor.CurWaypoint = (10, points[10]);
        replay.Executor.TryCloseSkipOtherOperations();
        Assert.True(replay.Executor.ShouldExecuteWaypointAction(points[10]));
    }

    [Fact]
    public void HealingRestartNeedsTheOriginalTeleportAndAReplaySafePrefix()
    {
        var replay = new PathReplay();
        var points = Enumerable.Range(0, 4).Select(_ => replay.Point("walk")).ToList();
        Assert.False(PathExecutor.CanRestartAfterHealing(points, 3));
        points[0].Type = "teleport";
        Assert.True(PathExecutor.CanRestartAfterHealing(points, 3));
        points[1].Action = "combat_script";
        Assert.False(PathExecutor.CanRestartAfterHealing(points, 3));
        Assert.True(PathExecutor.CanRestartAfterHealing(points, 1)); // 此节点尚未执行，不属于回放区间。
    }

    [Theory]
    [InlineData("fresh", true)]
    [InlineData("old", false)]
    [InlineData("foreign", false)]
    [InlineData("low", false)]
    [InlineData("not-ordinary", false)]
    public async Task StatueReturnAloneCannotSignAHealingRestart(string observation, bool confirmed)
    {
        var clock = new FakeTimeProvider();
        var source = new CaptureFrameSource(clock); var foreign = new CaptureFrameSource(clock);
        var before = source.Next();
        var recovered = 0;
        var error = await Record.ExceptionAsync(() => PathExecutor.ConfirmHealingRestartAsync(before,
            () => { recovered++; clock.Advance(TimeSpan.FromSeconds(1)); return Task.CompletedTask; },
            () =>
            {
                clock.Advance(TimeSpan.FromMilliseconds(1));
                var stamp = observation == "old" ? before : observation == "foreign" ? foreign.Next() : source.Next();
                return new HealingFrame(stamp, observation != "not-ordinary", observation == "low");
            }, ms => { clock.Advance(TimeSpan.FromMilliseconds(ms)); return Task.CompletedTask; }, default, clock));
        Assert.Equal(1, recovered);
        if (confirmed) Assert.IsType<PathExecutor.HealingRecoveryCompletedException>(error);
        else Assert.IsType<InvalidOperationException>(error);
    }

    [Fact]
    public async Task ConfirmedHealingOnLastOrdinaryAttemptCanRestartAtItsExistingCheckpoint()
    {
        var attempts = 0;
        var releases = 0;
        var ended = await PathExecutor.ExecuteSegmentWithRetriesAsync(() =>
        {
            if (++attempts == 1) throw new RetryException("ordinary failure");
            if (attempts == 2) throw new PathExecutor.HealingRecoveryCompletedException();
            return Task.CompletedTask;
        }, _ => { }, () => releases++, default);
        Assert.False(ended);
        Assert.Equal(3, attempts);
        Assert.Equal(3, releases);
    }

    [Fact]
    public async Task ExhaustedRetryCannotContinueToTheNextSegmentOrReportRouteSuccess()
    {
        var attempts = 0;
        var releases = 0;
        var continued = false;
        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await PathExecutor.ExecuteSegmentWithRetriesAsync(() =>
            {
                attempts++;
                throw new RetryException("未完成战斗点");
            }, _ => { }, () => releases++, default);
            continued = true;
        });
        Assert.Equal(2, attempts);
        Assert.Equal(2, releases);
        Assert.False(continued);
        Assert.IsType<RetryException>(error.InnerException);
    }

    [Fact]
    public async Task RetryThenSuccessKeepsNormalCompletionAndAlwaysReleasesInput()
    {
        var attempts = 0;
        var releases = 0;
        var endedEarly = await PathExecutor.ExecuteSegmentWithRetriesAsync(() =>
        {
            if (++attempts == 1) throw new RetryException("定位暂不可用");
            return Task.CompletedTask;
        }, _ => { }, () => releases++, default);
        Assert.False(endedEarly);
        Assert.Equal(2, attempts);
        Assert.Equal(2, releases);
    }

    [Fact]
    public async Task OnlyExplicitEndConditionMayCompleteARouteEarly()
    {
        Assert.True(await PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => throw new PathExecutor.EndConditionSatisfiedException(), _ => { }, () => { }, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => throw new HandledException("无法识别点位，放弃路径"), _ => { }, () => { }, default));
    }

    [Fact]
    public async Task CancellationAndCombatExecutionFailureCannotBecomeSuccessfulRoutes()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => throw new Exception("取消后不得执行路径"), _ => { }, () => { }, cts.Token));
        var expected = new InvalidOperationException("战斗未确认结束");
        var retries = 0;
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => PathExecutor.ExecuteSegmentWithRetriesAsync(
            () => throw expected, _ => retries++, () => { }, default));
        Assert.Same(expected, actual);
        Assert.Equal(0, retries);
    }
}
