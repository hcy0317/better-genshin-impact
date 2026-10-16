using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.LogParse;
using Microsoft.ClearScript.V8;

namespace BetterGenshinImpact.UnitTest.CoreTests.ScriptTests;

public class MissingRouteActorTests
{
    private static void Validate(string script, string[]? party = null) => CombatScriptHandler.ValidateRouteRequirements(
        [new Waypoint { Id = 3, Action = "combat_script", ActionParams = script }], "角色限定路线",
        party ?? ["钟离", "芙宁娜", "那维莱特", "琴"]);

    [Fact]
    public void OnlyTypedRouteMissingActorsCanBeSkipped()
    {
        var missing = Record.Exception(() => Validate("玛薇卡 e(hold)"))!;
        Assert.True(BetterGenshinImpact.GameTask.AutoTrackPath.PathingTargetUnavailableException.IsUnavailableTarget(missing));
        Assert.False(BetterGenshinImpact.GameTask.AutoTrackPath.PathingTargetUnavailableException.IsUnavailableTarget(
            new InvalidOperationException(missing.Message))); // Message text is never authorization.
        Assert.False(BetterGenshinImpact.GameTask.AutoTrackPath.PathingTargetUnavailableException.IsUnavailableTarget(
            Record.Exception(() => Validate("玛薇卡 e(hold)", []))!));
        foreach (var other in new Exception[] { new OperationCanceledException(), new IOException("unrelated"),
                     new BetterGenshinImpact.GameTask.CombatNotFinishedException("not confirmed") })
            Assert.False(BetterGenshinImpact.GameTask.AutoTrackPath.PathingTargetUnavailableException.IsUnavailableTarget(
                new AggregateException(missing, other)));
        // Existing mixed-template optional alternatives do not become mandatory.
        Validate("玛薇卡 e;钟离 e(hold)");
        Validate("keypress(f);玛薇卡 e;芙宁娜 e");
    }

    [Fact]
    public async Task RecoveryFailureStillPreventsContinuingAfterAMissingActor()
    {
        var continued = false;
        await Assert.ThrowsAsync<BetterGenshinImpact.GameTask.TaskFailureRecoveryException>(async () =>
        {
            await ScriptStepOutcomeRunner.RunAsync(() =>
            {
                Validate("玛薇卡 e(hold)");
                return Task.FromResult(new ScriptExecutionResult(ScriptOutcomeKind.Completed, "unexpected"));
            }, _ => throw new IOException("handoff failed"), default);
            continued = true;
        });
        Assert.False(continued);
    }

    [Fact]
    public async Task MissingRequiredRouteActorSkipsAndTheNextRouteStillRunsWithoutSuccessCredit()
    {
        var moves = 0;
        var recovered = 0;
        var missing = await ScriptStepOutcomeRunner.RunAsync(() =>
        {
            Validate("玛薇卡 e(hold),keydown(w),wait(.1),keypress(VK_SPACE)");
            moves++;
            return Task.FromResult(new ScriptExecutionResult(ScriptOutcomeKind.Completed, "route"));
        }, _ => { recovered++; return Task.CompletedTask; }, default);
        Assert.Equal(ScriptOutcomeKind.Skipped, missing.Outcome.Kind);
        Assert.Equal(0, moves);
        Assert.Equal(1, recovered); // Existing safe handoff still applies, not a new retry.
        var record = new ExecutionRecord();
        missing.Outcome.ApplyTo(record);
        Assert.False(record.IsSuccessful);
        var next = await ScriptStepOutcomeRunner.RunAsync(() =>
        {
            Validate("钟离 e(hold)");
            moves++;
            return Task.FromResult(new ScriptExecutionResult(ScriptOutcomeKind.Completed, "next-route"));
        }, _ => throw new Exception("next route should not recover"), default);
        Assert.Equal(ScriptOutcomeKind.Completed, next.Outcome.Kind);
        Assert.Equal(1, moves);
    }

    [Fact]
    public async Task MissingActorKeepsTheExistingJsRouteSkipMarkerAndCannotWriteSuccessCooldown()
    {
        var api = new AutoPathingScript(Path.GetTempPath(), null, new LimitedFile(Path.GetTempPath()), (_, _) => { },
            (script, _) => { Validate(script); return Task.FromResult(true); }, (_, _) => { });
        using var engine = new V8ScriptEngine(V8ScriptEngineFlags.EnableTaskPromiseConversion);
        var result = "";
        engine.AddHostObject("pathing", api);
        engine.AddHostObject("report", (Action<string>)(value => result = value));
        await (Task)engine.Evaluate("(async()=>{let cooldown=0,skipped=0,next=0;try{await pathing.Run('玛薇卡 e(hold)');cooldown++;}catch(e){if(!e.message.startsWith('[BGI_PATH_TARGET_UNAVAILABLE]'))throw e;skipped++;}await pathing.Run('钟离 e');next++;report(`${cooldown}/${skipped}/${next}`);})()");
        Assert.Equal("0/1/1", result);
    }
}
