using BetterGenshinImpact.GameTask;
using Microsoft.ClearScript.V8;
using BetterGenshinImpact.Core.Script.Dependence;
using Microsoft.ClearScript;
using Microsoft.ClearScript.JavaScript;
using BetterGenshinImpact.GameTask.LogParse;
using BetterGenshinImpact.ViewModel.Pages;
using BetterGenshinImpact.GameTask.Common.Ui;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class TaskExecutionScopeTests
{
    [Fact]
    public void BackgroundInputCannotBypassTheSameTaskTerminalState()
    {
        using var owned = TaskExecutionScope.BeginOwned();
        var input = new BetterGenshinImpact.Core.Script.Dependence.Simulator.PostMessage(null);
        var error = new CombatNotFinishedException("not-ended");
        TaskExecutionScope.Capture().Report(error);
        Assert.Same(error, Assert.Throws<CombatNotFinishedException>(() => input.KeyPress("VK_F1")));
        Assert.Same(error, Assert.Throws<CombatNotFinishedException>(() => input.Click()));
    }

    [Fact]
    public async Task JavascriptReceivesUiTimeoutContextRatherThanTheInternalDeadlineCancellation()
    {
        var clock = new FakeTimeProvider();
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        var message = "";
        engine.AddHostObject("exitBook", (Func<Task>)(() => UiOperation.RunAsync(
            "return-main", TimeSpan.FromMilliseconds(100), default, async operation =>
            {
                operation.Observe(new(1) { Handbook = true }, UiTarget.Main);
                var wait = operation.DelayAsync(1000, default);
                clock.Advance(TimeSpan.FromMilliseconds(200));
                await wait;
                return true;
            }, clock: clock)));
        engine.AddHostObject("report", (Action<string>)(value => message = value));
        await (Task)engine.Evaluate("(async () => { try { await exitBook(); } catch (e) { report(e.message); } })()");
        Assert.Contains("return-main", message);
        Assert.Contains("handbook=True", message);
        Assert.DoesNotContain("was canceled", message);
    }

    [Fact]
    public void UnexpectedFightExitRemainsTerminalUnlessEndWasAlreadyConfirmed()
    {
        var original = new TimeoutException("结束检测未能完成");
        var failure = Assert.Throws<CombatNotFinishedException>(() =>
            TaskExecutionScope.RethrowCombatFailure(original, true, false, default));
        Assert.Same(original, failure.InnerException);
        Assert.Same(original, Assert.Throws<TimeoutException>(() =>
            TaskExecutionScope.RethrowCombatFailure(original, true, true, default)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnfinishedBattleDoesNotTriggerOneDragonCompletionActions(bool finalCheck)
    {
        var completed = false;
        var error = new CombatNotFinishedException("尚未结束");
        await Assert.ThrowsAsync<CombatNotFinishedException>(() => finalCheck
            ? OneDragonFinalizer.RunAsync(() => throw error, () => completed = true)
            : OneDragonFinalizer.RunWithFailureCompletionAsync(() => throw error, () => completed = true));
        Assert.False(completed);
    }

    [Fact]
    public void FailedTaskCannotLeaveSuccessCreditOrPoisonTheNextIndependentTask()
    {
        var previous = TaskExecutionScope.BeginOwned();
        var oldGuard = TaskExecutionScope.Capture();
        var failure = new CombatNotFinishedException("old-battle");
        oldGuard.Report(failure);
        var record = new ExecutionRecord { IsSuccessful = true };
        ExecutionRecordFinalizer.Complete(record, DateTimeOffset.Now, DateTime.Now);
        Assert.False(record.IsSuccessful);
        previous.Dispose();

        using var current = TaskExecutionScope.BeginOwned();
        var inputs = 0;
        oldGuard.Report(new CombatNotFinishedException("late-callback"));
        Assert.ThrowsAny<OperationCanceledException>(() => oldGuard.Bind((Action)(() => inputs++))());
        Assert.Null(TaskExecutionScope.Failure);
        TaskExecutionScope.Capture().Bind((Action)(() => inputs++))();
        Assert.Equal(1, inputs);
    }

    [Fact]
    public async Task UnconfirmedCombatNeverRunsUiOrRevivalRecovery()
    {
        var recovered = false;
        var error = new CombatNotFinishedException("not-finished");
        await Assert.ThrowsAsync<CombatNotFinishedException>(() => TaskFailureRecoveryPolicy.RecoverOrThrowAsync(
            error, () => { recovered = true; return Task.CompletedTask; }));
        Assert.False(recovered);
        Assert.Same(error, TaskRunnerFailurePolicy.GetTerminationException(error, false, false));
    }

    [Fact]
    public async Task ModuleEvaluationDoesNotFinishBeforeItsEntrypointPromise()
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = false;
        engine.AddHostObject("hold", (Func<Task>)(() => gate.Task));
        engine.AddHostObject("done", (Action)(() => completed = true));
        var evaluation = engine.Evaluate(new DocumentInfo("task-lifetime") { Category = ModuleCategory.Standard },
            "(async function () { await hold(); done(); })()");
        var task = Assert.IsAssignableFrom<Task>(evaluation);
        try { Assert.False(task.IsCompleted); }
        finally { gate.TrySetResult(); }
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(completed);
    }

    [Fact]
    public async Task ActualPathingHostBlocksSecondRouteEvenWhenJavascriptSwallowsBothErrors()
    {
        using var owned = TaskExecutionScope.BeginOwned();
        var attempts = 0;
        var original = new CombatNotFinishedException("战斗未结束");
        var api = new AutoPathingScript(Path.GetTempPath(), null, new LimitedFile(Path.GetTempPath()),
            (_, _) => { }, (_, _) => { attempts++; throw original; }, captureFailure: (_, _) => { });
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        engine.AddHostObject("pathing", api);
        var failure = await Assert.ThrowsAsync<CombatNotFinishedException>(() => TaskExecutionScope.RunCheckedAsync(
            async () => await (Task)engine.Evaluate("""
                (async () => {
                    try { await pathing.Run('{}'); } catch (e) {}
                    try { await pathing.Run('{}'); } catch (e) {}
                })()
                """)));
        Assert.Same(original, failure);
        Assert.Equal(1, attempts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JavascriptCannotSwallowOrRepackageTerminalCombatAndContinueInput(bool repackage)
    {
        using var owned = TaskExecutionScope.BeginOwned();
        var guard = TaskExecutionScope.Capture();
        var original = new CombatNotFinishedException("敌人仍在场");
        var inputs = 0;
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        engine.AddHostObject("fight", guard.Bind((Func<Task>)(async () =>
        {
            await Task.Yield();
            throw original;
        })));
        engine.AddHostObject("moveNext", guard.Bind((Action)(() => inputs++)));
        var code = "(async () => { try { await fight(); } catch (e) { "
            + (repackage ? "try { throw new Error(String(e)); } catch (wrapped) {}" : "")
            + " } try { moveNext(); } catch (e) {} })()";
        var error = await Assert.ThrowsAsync<CombatNotFinishedException>(() =>
            TaskExecutionScope.RunCheckedAsync(async () => await (Task)engine.Evaluate(code)));
        Assert.Same(original, error);
        Assert.Equal(0, inputs);
    }
}
