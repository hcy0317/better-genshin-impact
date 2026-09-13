using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.LogParse;
using Microsoft.ClearScript.V8;
using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class ScriptOutcomeTests
{
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
