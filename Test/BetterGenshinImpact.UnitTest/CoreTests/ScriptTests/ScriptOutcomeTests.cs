using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.LogParse;
using Microsoft.ClearScript.V8;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.TaskProgress;
using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class ScriptOutcomeTests
{
    [Fact]
    public async Task JavaScriptRethrowPreservesNativeTerminalRecoveryFailure()
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        var terminal = new TaskFailureRecoveryException(new InvalidOperationException("step"), new TimeoutException("main UI"));
        engine.AddHostObject("recover", (Func<Task>)(() => Task.FromException(terminal)));
        var actual = await Record.ExceptionAsync(() => ScriptOutcomeHost.RunAsync(engine,
            () => engine.Evaluate("(async () => { try { await recover(); } catch (error) { throw error; } })()"), default));
        Assert.NotNull(actual);
        Assert.True(TaskFailureRecoveryPolicy.IsTerminalFailure(actual));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActualScriptAndStepPipelineOnlyContinuesAfterConfirmedRecovery(bool recoverable)
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        using var ownership = new SemaphoreSlim(1, 1);
        var nextStarted = false;
        var summary = new ScriptOutcomeAccumulator();
        async Task Run()
        {
            await ownership.WaitAsync();
            try
            {
                var step = await ScriptStepOutcomeRunner.RunAsync(
                    () => ScriptOutcomeHost.RunAsync(engine,
                        () => engine.Evaluate("taskResult.report('NeedsReconcile', 'NOT_CLOSED')"), default),
                    _ =>
                    {
                        Assert.Equal(0, ownership.CurrentCount);
                        return recoverable ? Task.CompletedTask : Task.FromException(new TimeoutException("main UI unconfirmed"));
                    }, default);
                summary.Add("first", step.Outcome);
                nextStarted = true;
                summary.Add("next", await ScriptOutcomeHost.RunAsync(engine,
                    () => engine.Evaluate("taskResult.report('Completed', 'DONE')"), default));
            }
            finally { ownership.Release(); }
        }
        if (recoverable)
        {
            await Run();
            Assert.True(nextStarted);
            Assert.Equal(ScriptOutcomeKind.NeedsReconcile, summary.Complete().Kind);
        }
        else
        {
            await Assert.ThrowsAsync<TaskFailureRecoveryException>(Run);
            Assert.False(nextStarted);
        }
        Assert.Equal(1, ownership.CurrentCount);
    }

    [Fact]
    public async Task CancellationDuringRecoveryCannotReturnAnActionableStep()
    {
        using var ct = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScriptStepOutcomeRunner.RunAsync(
            () => Task.FromResult(new ScriptExecutionResult(ScriptOutcomeKind.Deferred, "BUSY")),
            _ => { ct.Cancel(); return Task.CompletedTask; }, ct.Token));
    }

    [Fact]
    public void LaterCompletionCannotMoveTheResumeCheckpointPastAnEarlierIncompleteTask()
    {
        var progress = new TaskProgress
        {
            CurrentScriptGroupName = "group",
            CurrentScriptGroupProjectInfo = new() { GroupName = "group", Name = "first", Outcome = "NeedsReconcile" }
        };
        ScriptTaskProgressFinalizer.CompleteCurrentProject(progress, DateTime.Now);
        progress.CurrentScriptGroupProjectInfo = new() { GroupName = "group", Name = "second", Outcome = "Completed" };
        ScriptTaskProgressFinalizer.CompleteCurrentProject(progress, DateTime.Now);
        Assert.Null(progress.LastSuccessScriptGroupProjectInfo);
        Assert.Equal(2, progress.History!.Count);
        Assert.Equal(1, progress.History[1].Status);
    }

    [Fact]
    public void AllSkippedChildrenRemainSkippedRatherThanCompleted()
    {
        var summary = new ScriptOutcomeAccumulator();
        summary.Add("one", new(ScriptOutcomeKind.Skipped, "CD_ACTIVE"));
        summary.Add("two", new(ScriptOutcomeKind.Skipped, "DISABLED"));
        var result = summary.Complete();
        Assert.Equal(ScriptOutcomeKind.Skipped, result.Kind);
        Assert.Equal(2, result.Children.Count);
    }

    [Fact]
    public async Task WrappedCancellationCannotBecomeARecoverableScriptFailure()
    {
        var original = new InvalidOperationException("script wrapper", new OperationCanceledException("cancelled"));
        var recovered = false;
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => ScriptStepOutcomeRunner.RunAsync(
            () => Task.FromException<ScriptExecutionResult>(original),
            _ => { recovered = true; return Task.CompletedTask; }, default));
        Assert.Same(original, actual);
        Assert.False(recovered);
    }

    [Fact]
    public async Task IncompleteRealScriptMustRecoverBeforeReturningItsUnchangedOutcome()
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        var mainUiConfirmed = false;
        var step = await ScriptStepOutcomeRunner.RunAsync(
            () => ScriptOutcomeHost.RunAsync(engine,
                () => engine.Evaluate("taskResult.report('NeedsReconcile', 'CONSUMPTION_UNKNOWN')"), default),
            _ => { mainUiConfirmed = true; return Task.CompletedTask; }, default);
        Assert.True(mainUiConfirmed);
        Assert.Equal(ScriptOutcomeKind.NeedsReconcile, step.Outcome.Kind);
        Assert.Equal("CONSUMPTION_UNKNOWN", step.Outcome.Reason);
        Assert.Null(step.RecoveredFailure);
    }

    [Theory]
    [InlineData("Deferred")]
    [InlineData("NeedsReconcile")]
    [InlineData("Skipped")]
    public void TypedNonCompletionCannotAdvanceTheSuccessfulResumeCheckpoint(string kind)
    {
        var progress = new TaskProgress
        {
            CurrentScriptGroupName = "group",
            CurrentScriptGroupProjectInfo = new() { Name = "child", Outcome = kind, Status = 1 }
        };
        ScriptTaskProgressFinalizer.CompleteCurrentProject(progress, DateTime.Now);
        Assert.Null(progress.LastSuccessScriptGroupProjectInfo);
        Assert.NotEqual(1, progress.CurrentScriptGroupProjectInfo.Status);
        Assert.Equal(0, progress.ConsecutiveFailureCount);
        Assert.True(progress.CurrentScriptGroupProjectInfo.TaskEnd);
    }

    [Theory]
    [InlineData("Deferred")]
    [InlineData("NeedsReconcile")]
    public async Task CompletedSiblingCannotHideAnIncompleteRealJavaScriptOutcome(string kind)
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        var summary = new ScriptOutcomeAccumulator();
        summary.Add("waiting-child", await ScriptOutcomeHost.RunAsync(engine,
            () => engine.Evaluate($"taskResult.report('{kind}', 'ACTION_NOT_CLOSED')"), default));
        summary.Add("completed-child", await ScriptOutcomeHost.RunAsync(engine,
            () => engine.Evaluate("taskResult.report('Completed', 'DONE')"), default));

        var parent = summary.Complete();
        var record = new ExecutionRecord { IsSuccessful = true };
        parent.ApplyTo(record);
        Assert.Equal(kind, parent.Kind.ToString());
        Assert.False(record.IsSuccessful);
        Assert.Equal("ACTION_NOT_CLOSED", parent.Children[0].Reason);
        Assert.Equal("waiting-child", parent.Children[0].TaskName);
        Assert.Equal(2, parent.Children.Count);
    }

    [Theory]
    [InlineData("Completed", true)]
    [InlineData("Skipped", false)]
    [InlineData("Deferred", false)]
    [InlineData("NeedsReconcile", false)]
    [InlineData("Failed", false)]
    [InlineData("Cancelled", false)]
    public async Task OnlyExplicitCompletedIsSuccessful(string kind, bool successful)
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding | V8ScriptEngineFlags.EnableTaskPromiseConversion);
        var result = await ScriptOutcomeHost.RunAsync(engine,
            () => engine.Evaluate($"(async()=>{{taskResult.report('{kind}','reason')}})()"), default);
        var record = new ExecutionRecord();
        result.ApplyTo(record);
        Assert.Equal(successful, record.IsSuccessful);
        Assert.Equal(kind, record.Outcome);
    }

    [Fact]
    public async Task LegacyNormalReturnAndManagedMissingReportRemainDifferent()
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        var legacy = await ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("42"), default);
        var managed = await ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("taskResult.requireExplicitOutcome()"), default);
        Assert.Equal(ScriptOutcomeKind.Completed, legacy.Kind);
        Assert.Equal(ScriptOutcomeKind.NeedsReconcile, managed.Kind);
    }

    [Fact]
    public async Task AClosedReportObjectCannotOverwriteTheNextExecution()
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        await ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("globalThis.old=taskResult;taskResult.report('Skipped','old')"), default);
        var current = await ScriptOutcomeHost.RunAsync(engine, () => engine.Evaluate("try{old.report('Completed','late')}catch(e){};taskResult.report('Deferred','new')"), default);
        Assert.Equal(new ScriptExecutionResult(ScriptOutcomeKind.Deferred, "new"), current);
    }

    [Fact]
    public async Task CompletedReportCannotClearTheCombatTerminationLock()
    {
        using var execution = TaskExecutionScope.BeginOwned();
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding);
        await Assert.ThrowsAsync<CombatNotFinishedException>(() => ScriptOutcomeHost.RunAsync(engine, () =>
        {
            engine.Evaluate("taskResult.report('Completed','claimed')");
            try { TaskExecutionScope.StopUnconfirmedCombat("unconfirmed"); } catch (CombatNotFinishedException) { }
            return null;
        }, default));
    }

    [Fact]
    public async Task ARealJavaScriptDeferredResultCannotBecomeASuccessfulExecutionRecord()
    {
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding | V8ScriptEngineFlags.EnableTaskPromiseConversion);
        var result = await ScriptOutcomeHost.RunAsync(engine,
            () => engine.Evaluate("taskResult.report('Deferred', 'BUSY')"), default);
        var record = new ExecutionRecord { IsSuccessful = true };
        result.ApplyTo(record);
        Assert.False(record.IsSuccessful);
        Assert.Equal("Deferred", record.Outcome);
        Assert.Equal("BUSY", record.OutcomeReason);
    }
}
