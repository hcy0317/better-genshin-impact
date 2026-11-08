using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoFight;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ClearScript.V8;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class CombatRecoveryTests
{
    [Fact]
    public async Task VerifiedRecoverySignalMustRetainTheOriginalBoundObservationForRouteAssessment()
    {
        BetterGenshinImpact.Helpers.ApplicationHostBootstrapGuard.ProhibitForCurrentProcess();
        using var taskOwner = TaskExecutionScope.BeginOwned();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var producer = new Fischless.GameCapture.CaptureFrameSource(clock);
        using var routeOwner = BetterGenshinImpact.GameTask.Common.Ui.UiOperation.Begin(
            "revive-proof-route-parent", TimeSpan.FromSeconds(240),
            logger: NullLogger.Instance, clock: clock);
        var originalDeadline = clock.GetUtcNow() + routeOwner.Remaining;
        var recovered = 0;
        var observations = 0;
        long recoveryCompleted = 0;
        ReviveRecoveryProof? published = null;
        Task Recover()
        {
            recovered++;
            clock.Advance(TimeSpan.FromSeconds(5));
            recoveryCompleted = clock.GetTimestamp();
            return Task.CompletedTask;
        }
        Task<ReviveRecoveryProof> Verify()
        {
            observations++;
            clock.Advance(TimeSpan.FromMilliseconds(61));
            var target = new ReviveTarget("钟离", 1);
            var frame = new ReviveRecoveryFrame(0, true,
                BetterGenshinImpact.GameTask.Common.BgiVision.ReviveUiState.None, 1,
                System.Collections.Immutable.ImmutableDictionary<int, string>.Empty.Add(1, target.Name))
                .WithSource(producer.Next(), clock);
            var proof = new ReviveRecoveryProof(frame, target, false, routeOwner.RootId, routeOwner.Id, recoveryCompleted);
            published = proof;
            return Task.FromResult(proof);
        }
        // Task<T> passes through the production Func<Task> boundary, exactly where the current result is erased.
        var signal = await Assert.ThrowsAsync<CombatRecoveryCompletedException>(() =>
            CombatRecoveryCompletedException.RecoverVerifiedAsync(Recover, Verify, routeOwner.Token));
        Assert.Equal(1, recovered);
        Assert.Equal(1, observations);
        Assert.NotNull(published);
        Assert.True(published.Frame.SourceBound);
        Assert.True(published.Frame.HasUsableEvidence);
        Assert.NotNull(published.Target);
        Assert.True(published.Frame.Confirms(published.Target));
        Assert.False(published.LowHp);
        Assert.True(published.Frame.SourceStamp.CapturedTimestamp > recoveryCompleted);
        Assert.Equal(routeOwner.RootId, published.UiRootId);
        Assert.Equal(originalDeadline, clock.GetUtcNow() + routeOwner.Remaining);
        TaskExecutionScope.ThrowIfFailed();
        Assert.Same(published, signal.Proof); // Business RED: the existing completed path drops the verified value.
    }

    [Fact]
    public async Task JavascriptCanRetryTheCommissionAfterConfirmedRecoveryButNeverCreditTheFailedAttempt()
    {
        var recovered = await Assert.ThrowsAsync<CombatRecoveryCompletedException>(() =>
            CombatRecoveryCompletedException.RecoverAsync(() => Task.CompletedTask, () => true, () => Task.CompletedTask, default));
        using var owner = TaskExecutionScope.BeginOwned();
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        var attempts = 0;
        var credits = 0;
        engine.AddHostObject("execute", (Func<Task>)(() =>
        {
            TaskExecutionScope.ThrowIfFailed();
            attempts++;
            if (attempts == 1) TaskExecutionScope.RethrowCombatFailure(recovered, true, false, default);
            return Task.CompletedTask;
        }));
        engine.AddHostObject("credit", (Action)(() => credits++));
        await (Task)engine.Evaluate("(async () => { for (let i=0;i<2;i++) { let success=false; try { await execute(); success=true; } catch(e) {} if(success) { credit(); break; } } })()");
        Assert.Equal(2, attempts);
        Assert.Equal(1, credits);
        TaskExecutionScope.ThrowIfFailed();
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnstableRecoveryCannotProduceARetrySignal(bool first, bool second)
    {
        var checks = new Queue<bool>([first, second]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask, () => checks.Dequeue(), () => Task.CompletedTask, default));
    }

    [Fact]
    public async Task RecoveryFailureAndCancellationAreNotReportedAsCompleted()
    {
        var fault = new TimeoutException("teleport failed");
        Assert.Same(fault, await Assert.ThrowsAsync<TimeoutException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => throw fault, () => true, () => Task.CompletedTask, default)));
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CombatRecoveryCompletedException.RecoverAsync(
            () => Task.CompletedTask, () => true, () => { cts.Cancel(); return Task.CompletedTask; }, cts.Token));
    }

    [Fact]
    public async Task ConfirmedRecoveryDoesNotClearAnExistingTerminalFailure()
    {
        var recovered = await Assert.ThrowsAsync<CombatRecoveryCompletedException>(() =>
            CombatRecoveryCompletedException.RecoverAsync(() => Task.CompletedTask, () => true, () => Task.CompletedTask, default));
        using var owner = TaskExecutionScope.BeginOwned();
        var terminal = new CombatNotFinishedException("still unknown");
        TaskExecutionScope.Capture().Report(terminal);
        Assert.Same(terminal, Assert.Throws<CombatNotFinishedException>(() =>
            TaskExecutionScope.RethrowCombatFailure(recovered, true, false, default)));
    }
    [Fact]
    public async Task JsonPreActionsStopImmediatelyAfterConfirmedRecovery()
    {
        var recovered = await Assert.ThrowsAsync<CombatRecoveryCompletedException>(() =>
            CombatRecoveryCompletedException.RecoverAsync(() => Task.CompletedTask, () => true, () => Task.CompletedTask, default));
        var executed = new List<string>();
        var completed = false;
        await Assert.ThrowsAsync<CombatRecoveryCompletedException>(() => AutoFightJsonTask.RunPreActionSequenceAsync(
            ["recover", "must-not-run"], action => { executed.Add(action); throw recovered; },
            () => { completed = true; return Task.CompletedTask; }, NullLogger.Instance, default));
        Assert.Equal(["recover"], executed);
        Assert.False(completed);
    }
    [Fact]
    public async Task OnlyConfirmedRecoveryCanLeaveCombatWithoutPoisoningTheTask()
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var order = new List<string>();
        var recovered = await Assert.ThrowsAsync<CombatRecoveryCompletedException>(() =>
            CombatRecoveryCompletedException.RecoverAsync(
                () => { order.Add("recover"); return Task.CompletedTask; },
                () => { order.Add("confirm"); return true; },
                () => { order.Add("wait"); return Task.CompletedTask; }, default));
        Assert.Equal(["recover", "confirm", "wait", "confirm"], order);
        Assert.Same(recovered, Assert.Throws<CombatRecoveryCompletedException>(() =>
            TaskExecutionScope.RethrowCombatFailure(recovered, true, false, default)));
        TaskExecutionScope.ThrowIfFailed();
        Assert.Throws<CombatNotFinishedException>(() =>
            TaskExecutionScope.RethrowCombatFailure(new RetryException("not confirmed"), true, false, default));
    }
}
