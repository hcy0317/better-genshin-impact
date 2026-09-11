using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoFight;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.ClearScript.V8;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class CombatRecoveryTests
{
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
