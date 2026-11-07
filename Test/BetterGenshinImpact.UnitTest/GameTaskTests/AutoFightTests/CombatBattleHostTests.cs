using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public partial class CombatBattleHostTests
{
    private static CombatBattleObservation ProjectCurrentJitterBody(CaptureFrameStamp source,Guid battle,FakeTimeProvider clock,int height,bool centered=false)
    {
        var visual=centered?new EnemySeekVisual(928,height==6?375:376,64,height,64*height)
            :new EnemySeekVisual(287,height==6?464:465,67,height,67*height);
        var raw=AutoFightSeek.SelectSeekDecision([visual],1920,1080);
        Assert.Equal(SeekCueKind.HealthBar,raw.Cue);
        Assert.Equal(height==6?AutoFightSeekAction.KeepFighting:AutoFightSeekAction.ApproachVisibleEnemy,raw.Action);
        var passive=new BetterGenshinImpact.GameTask.AutoFight.Model.PassiveTargetObservation(true,false,clock.GetUtcNow().UtcDateTime,raw.Visual,1920,1080,null)
        {Source=source,BattleId=battle,CaptureEpoch=0,Quality=CombatObservationQuality.Available,SpatialCandidateCount=1,
            Control=new(MotionStatus.Unknown,false)};
        var result=NativeCombatBattleHostIo.ProjectObservation(passive,0,true,clock.GetUtcNow().UtcDateTime);
        Assert.Equal(EnemyIndicatorDirection.None,result.Target!.Value.Direction); // Original passive adapter, not a test normalization.
        return result;
    }

    [Fact]
    public async Task CurrentBodyHeightActionFlipKeepsTheSameUnsentCameraOnANewSource()
    {
        var clock=new FakeTimeProvider();using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId);using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        for(var step=0;step<2000&&host.State!="Searching";step++)await host.AdvanceAsync(flow,default);
        Assert.Equal("Searching",host.State);
        var attempts=new List<CombatBattleHostInput>();
        io.PrepareEvidence=input=>{if(input.Kind==CombatBattleHostInputKind.Camera)attempts.Add(input);return true;};
        io.TargetFactory=source=>ProjectCurrentJitterBody(source,flow.Context.BattleId,clock,6);
        for(var step=0;step<100&&attempts.Count==0;step++)await host.AdvanceAsync(flow,default);
        var pending=Assert.Single(attempts);var before=io.Traces.Last().Observation;
        Assert.Equal(AutoFightSeekAction.KeepFighting,before.Target!.Value.Action);
        Assert.Equal(CombatBattleHostInputStatus.NotSent,io.Traces.Last().InputStatus);
        Assert.Equal("awaiting-business-decision-frame",host.Reason);
        Assert.DoesNotContain(io.Inputs,input=>input.RequestId==pending.RequestId);
        clock.Advance(TimeSpan.FromMilliseconds(500)); // A legal fresh frame; the original request deadline remains live.
        Assert.True(clock.GetTimestamp()<pending.DeadlineTimestamp);
        io.TargetFactory=source=>ProjectCurrentJitterBody(source,flow.Context.BattleId,clock,4);
        Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        var current=io.Traces.Last().Observation;
        Assert.Equal(AutoFightSeekAction.ApproachVisibleEnemy,current.Target!.Value.Action);
        Assert.True(current.Source.IsFresh(clock,TimeSpan.FromMilliseconds(150)));
        Assert.True(current.Source.IsAfter(pending.Source));Assert.True(current.Source.CapturedTimestamp>pending.Source.CapturedTimestamp);
        Assert.Equal(before.Control,current.Control);Assert.Equal(before.Motion,current.Motion);Assert.Equal(before.CaptureEpoch,current.CaptureEpoch);
        Assert.Equal(before.Scene,current.Scene);Assert.Equal(before.SearchHint,current.SearchHint);
        Assert.Equal(before.DirectionConfirmationPending,current.DirectionConfirmationPending);
        Assert.Equal(before.Target.Value.Direction,current.Target.Value.Direction);Assert.Equal(1,current.Target.Value.SignalCount);
        var visual=current.Target.Value.Visual!.Value;
        Assert.Equal(pending.X,Math.Clamp(AutoFightSeek.GetVisibleEnemyCameraOffset(visual,1920),-120,120));
        Assert.Equal(pending.Y,AutoFightSeek.GetVisibleEnemyCameraVerticalOffset(visual,1080));
        var sent=Assert.Single(io.Inputs.Where(input=>input.Kind==CombatBattleHostInputKind.Camera));
        Assert.Equal(pending.RequestId,sent.RequestId);Assert.Equal(current.Source,sent.Source);
        Assert.Equal(-120,sent.X);Assert.Equal(pending.Y,sent.Y);Assert.Equal(1,host.CameraRequests);Assert.Equal(0,host.ApproachRequests);
    }

    [Fact]
    public async Task AnApproachReservationStillCancelsWhenTheOriginalDistanceActionLeavesAfterHysteresis()
    {
        var clock=new FakeTimeProvider();using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId);using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        for(var step=0;step<2000&&host.State!="Searching";step++)await host.AdvanceAsync(flow,default);
        var attempts=new List<CombatBattleHostInput>();var cancelled=0;
        io.PrepareEvidence=input=>{if(input.Kind==CombatBattleHostInputKind.Approach)attempts.Add(input);return true;};
        io.CancelEvidence=()=>cancelled++;
        io.TargetFactory=source=>ProjectCurrentJitterBody(source,flow.Context.BattleId,clock,4,true);
        for(var step=0;step<100&&attempts.Count==0;step++)await host.AdvanceAsync(flow,default);
        var pending=Assert.Single(attempts);Assert.Equal(AutoFightSeekAction.ApproachVisibleEnemy,io.Traces.Last().Observation.Target!.Value.Action);
        io.UnsentAttempts=1;io.TargetFactory=source=>ProjectCurrentJitterBody(source,flow.Context.BattleId,clock,6,true);
        Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        Assert.Contains(io.Requests,input=>input.RequestId==pending.RequestId);
        Assert.DoesNotContain(io.Inputs,input=>input.RequestId==pending.RequestId);
        clock.Advance(TimeSpan.FromMilliseconds(250)); // The preceding known NotSent already used the original292ms clock interval.
        Assert.True(clock.GetTimestamp()<pending.DeadlineTimestamp);var before=cancelled;
        Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        Assert.Equal(AutoFightSeekAction.KeepFighting,io.Traces.Last().Observation.Target!.Value.Action);
        Assert.True(cancelled>before);Assert.DoesNotContain(io.Inputs,input=>input.RequestId==pending.RequestId);
        Assert.Equal(0,host.ApproachRequests);
    }

    [Fact]
    public async Task DisappearingCurrentBodyStillWithdrawsItsUnsentCamera()
    {
        var clock=new FakeTimeProvider();using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId);using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        for(var step=0;step<2000&&host.State!="Searching";step++)await host.AdvanceAsync(flow,default);
        var attempts=new List<CombatBattleHostInput>();var cancelled=0;
        io.PrepareEvidence=input=>{if(input.Kind==CombatBattleHostInputKind.Camera)attempts.Add(input);return true;};io.CancelEvidence=()=>cancelled++;
        io.TargetFactory=source=>ProjectCurrentJitterBody(source,flow.Context.BattleId,clock,6);
        for(var step=0;step<100&&attempts.Count==0;step++)await host.AdvanceAsync(flow,default);
        var pending=Assert.Single(attempts);var before=cancelled;clock.Advance(TimeSpan.FromMilliseconds(500));
        io.TargetFactory=source=>new(source,flow.Context.BattleId,CombatObservationQuality.Available,null,1920,1080){Control=new(MotionStatus.Unknown,false)};
        Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        Assert.True(cancelled>before);Assert.DoesNotContain(io.Inputs,input=>input.RequestId==pending.RequestId);
        Assert.Equal(0,host.CameraRequests);Assert.Equal(0,host.ApproachRequests);
    }

    [Fact]
    public async Task FinalLateBodyWaitCrossingFortyFiveCannotResumeFromNextFreshBody()
    {
        var clock=new FakeTimeProvider();var game=new ReturningGame(clock);using var flow=CreateFlow(false,game,clock);
        var io=new ReplayIo(clock,flow.Context.BattleId){SourcePeriodMilliseconds=50};
        using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        var body=AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(783,370,61,5,305)],1920,1080);
        Assert.Equal(SeekCueKind.HealthBar,body.Cue);
        CombatBattleObservation Body(CaptureFrameStamp source)=>new(source,flow.Context.BattleId,CombatObservationQuality.Available,body,1920,1080)
            {Control=new(MotionStatus.Unknown,false)};
        var progressStarted=clock.GetTimestamp();io.TargetFactory=Body;
        await host.AdvanceAsync(flow,default);io.TargetFactory=null;
        var originalProgress=io.Traces.Last().LastProgress;Assert.NotNull(originalProgress);
        var nearDeadline=false;
        for(var step=0;step<2000&&!(host.CameraRequests==24&&host.State=="AwaitParty");step++)
        {
            if(!nearDeadline&&host.CameraRequests==24&&host.State=="Searching")
            {
                clock.Advance(TimeSpan.FromSeconds(43-clock.GetElapsedTime(progressStarted).TotalSeconds));
                nearDeadline=true;
            }
            Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        }
        Assert.True(nearDeadline);Assert.Equal("AwaitParty",host.State);
        Assert.Equal(24,io.Inputs.Count(input=>input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset));
        clock.Advance(TimeSpan.FromSeconds(44.98-.2166-clock.GetElapsedTime(progressStarted).TotalSeconds));
        CaptureFrameStamp late=default;PartySetupFinishObservation negative=default;
        io.AfterTargetCapture=()=>clock.Advance(TimeSpan.FromMilliseconds(216.6));
        io.TargetFactory=source=>{late=source;return Body(source);};
        io.PartyFactory=source=>negative=new(source.Sequence,source.CapturedAt,1920,1080,false,(ulong)source.Sequence){Source=source};
        Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        io.AfterTargetCapture=null;io.TargetFactory=_=>Body(late);
        Assert.Equal("CloseParty",host.State);
        Assert.InRange(clock.GetElapsedTime(progressStarted).TotalSeconds,44.979999,44.980001);
        Assert.InRange(clock.GetElapsedTime(late.CapturedTimestamp).TotalMilliseconds,216.599,216.601);
        Assert.True(negative.Source.IsFresh(clock,TimeSpan.FromMilliseconds(150)));Assert.False(negative.BarVisible);
        var opened=io.CompletedInputs.Last(input=>input.Input.Kind==CombatBattleHostInputKind.OpenParty);
        Assert.True(new CaptureFrameFence(opened.Input.Source,opened.Completed).Accepts(negative.Source));
        Assert.Equal(originalProgress,io.Traces.Last().LastProgress);
        Assert.DoesNotContain(io.Traces,trace=>trace.State=="Reengaging");
        var inputs=io.Inputs.Count;var attacks=game.Inputs;var waits=io.DelayCalls;var startedWait=clock.GetTimestamp();
        Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        Assert.Equal("CloseParty",host.State);Assert.Equal(waits+1,io.DelayCalls);
        Assert.Equal(50,clock.GetElapsedTime(startedWait).TotalMilliseconds);
        Assert.InRange(clock.GetElapsedTime(progressStarted).TotalSeconds,45.029999,45.030001);
        Assert.Equal(inputs,io.Inputs.Count);Assert.Equal(attacks,game.Inputs);
        io.TargetFactory=Body;
        var result=await host.AdvanceAsync(flow,default);
        Assert.Equal(originalProgress,io.Traces.Last().LastProgress);
        Assert.Equal(CombatBattleHostResult.Unconfirmed,result);
        Assert.NotEqual("target-returned-after-party-probe",host.Reason);
        Assert.Equal(inputs,io.Inputs.Count);Assert.Equal(attacks,game.Inputs);
        Assert.Equal(24,host.CameraRequests);Assert.Equal(0,host.ApproachRequests);
    }

    private static async Task<(EnemySeekDecision Body,CaptureFrameStamp Late,PartySetupFinishObservation Negative)> ReachFinalCloseWithNaturallyLateBody(
        FakeTimeProvider clock,NativeCombatFlowRunner flow,ReplayIo io,CombatBattleHost host,bool hasProgress=true,bool wrongPartyFence=false)
    {
        var body=AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(783,370,61,5,305)],1920,1080);
        Assert.Equal(SeekCueKind.HealthBar,body.Cue);
        CombatBattleObservation Body(CaptureFrameStamp source)=>new(source,flow.Context.BattleId,CombatObservationQuality.Available,body,1920,1080)
            {Control=new(MotionStatus.Unknown,false)};
        if(hasProgress){io.TargetFactory=Body;await host.AdvanceAsync(flow,default);}
        io.TargetFactory=null;
        for(var step=0;step<2000&&!(host.CameraRequests==24&&host.State=="AwaitParty");step++)
            Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        Assert.Equal(24,host.CameraRequests);Assert.Equal("AwaitParty",host.State);
        Assert.Equal(24,io.Inputs.Count(input=>input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset));
        CaptureFrameStamp latestBody=default;PartySetupFinishObservation negative=default;
        io.AfterTargetCapture=()=>{if(host.State=="AwaitParty")clock.Advance(TimeSpan.FromMilliseconds(216.6));};
        io.TargetFactory=source=>
        {
            if(host.State=="AwaitParty")latestBody=source;
            return Body(latestBody);
        };
        io.PartyFactory=source=>negative=new(source.Sequence,source.CapturedAt,1920,1080,false,(ulong)source.Sequence)
            {Source=wrongPartyFence?source with {SessionId=Guid.NewGuid()}:source};
        for(var step=0;step<100&&host.State!="CloseParty";step++)
            Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        io.AfterTargetCapture=null;
        Assert.Equal("CloseParty",host.State);Assert.True(latestBody.IsKnown);
        Assert.InRange(clock.GetElapsedTime(latestBody.CapturedTimestamp).TotalMilliseconds,216.599,216.601);
        Assert.False(latestBody.IsFresh(clock,TimeSpan.FromMilliseconds(150)));
        Assert.False(negative.BarVisible);Assert.True(negative.Source.IsFresh(clock,TimeSpan.FromMilliseconds(150)));
        var opened=io.CompletedInputs.Last(input=>input.Input.Kind==CombatBattleHostInputKind.OpenParty);
        var fence=new CaptureFrameFence(opened.Input.Source,opened.Completed);
        Assert.Equal(!wrongPartyFence,fence.Accepts(negative.Source));
        Assert.DoesNotContain(io.Traces,trace=>trace.State=="Reengaging");
        Assert.DoesNotContain(io.Inputs,input=>input.Kind==CombatBattleHostInputKind.CloseParty);
        return(body,latestBody,negative);
    }

    [Fact]
    public async Task FinalClosePartyWaitsOnceForLateBodyThenResumesFromTheNextFreshCurrent()
    {
        var clock=new FakeTimeProvider();var game=new ReturningGame(clock);using var flow=CreateFlow(false,game,clock);
        var io=new ReplayIo(clock,flow.Context.BattleId){SourcePeriodMilliseconds=50};
        using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        var scene=await ReachFinalCloseWithNaturallyLateBody(clock,flow,io,host);
        var inputs=io.Inputs.Count;var attacks=game.Inputs;var waits=io.DelayCalls;var before=clock.GetTimestamp();
        var progressAge=io.Traces.Last().ProgressAge;
        var progress=io.Traces.Last().LastProgress;Assert.NotNull(progress);
        Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        Assert.Equal("CloseParty",host.State);Assert.Equal(waits+1,io.DelayCalls);
        Assert.Equal(50,clock.GetElapsedTime(before).TotalMilliseconds);
        // This wait may be trace-throttled; check progress on the next phase-changing fresh observation.
        Assert.Equal(inputs,io.Inputs.Count);Assert.Equal(attacks,game.Inputs);
        Assert.Equal(scene.Late,io.Traces.Last().Observation.Source);Assert.NotNull(io.Traces.Last().Observation.Target);
        Assert.Equal(24,host.CameraRequests);Assert.Equal(0,host.ApproachRequests);
        io.TargetFactory=source=>new(source,flow.Context.BattleId,CombatObservationQuality.Available,scene.Body,1920,1080)
            {Control=new(MotionStatus.Unknown,false)};
        Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        Assert.Equal("Fighting",host.State);Assert.Equal("target-returned-after-party-probe",host.Reason);
        Assert.InRange(io.Traces.Last().ProgressAge-progressAge,.049999,.050001);
        Assert.Equal(progress,io.Traces.Last().LastProgress);
        Assert.Equal(inputs,io.Inputs.Count);Assert.Equal(attacks,game.Inputs);
        Assert.Equal(24,host.CameraRequests);Assert.Equal(0,host.ApproachRequests);
    }

    [Fact]
    public async Task LateBodyWaitCannotRenewTheIndependentNegativePartySample()
    {
        var clock=new FakeTimeProvider();var game=new ReturningGame(clock);using var flow=CreateFlow(false,game,clock);
        var io=new ReplayIo(clock,flow.Context.BattleId){SourcePeriodMilliseconds=50};
        using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        await ReachFinalCloseWithNaturallyLateBody(clock,flow,io,host);
        Assert.Equal(CombatBattleHostResult.Continue,await host.AdvanceAsync(flow,default));
        var inputs=io.Inputs.Count;var attacks=game.Inputs;var waits=io.DelayCalls;
        clock.Advance(TimeSpan.FromMilliseconds(101));
        Assert.Equal(CombatBattleHostResult.Unconfirmed,await host.AdvanceAsync(flow,default));
        Assert.Equal(inputs,io.Inputs.Count);Assert.Equal(attacks,game.Inputs);Assert.Equal(waits,io.DelayCalls);
        Assert.Equal(24,host.CameraRequests);Assert.Equal(0,host.ApproachRequests);
    }

    [Theory]
    [InlineData("no-progress")]
    [InlineData("stale-party")]
    [InlineData("party-fence")]
    [InlineData("deadline")]
    public async Task LateBodyCannotBypassTheOriginalFinalWaitEligibility(string invalid)
    {
        var clock=new FakeTimeProvider();var game=new ReturningGame(clock);using var flow=CreateFlow(false,game,clock);
        var io=new ReplayIo(clock,flow.Context.BattleId){SourcePeriodMilliseconds=50};
        using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        await ReachFinalCloseWithNaturallyLateBody(clock,flow,io,host,invalid!="no-progress",invalid=="party-fence");
        if(invalid=="stale-party")clock.Advance(TimeSpan.FromMilliseconds(151));
        if(invalid=="deadline")clock.Advance(TimeSpan.FromSeconds(Math.Max(0,45-io.Traces.Last().ProgressAge)));
        var inputs=io.Inputs.Count;var attacks=game.Inputs;var waits=io.DelayCalls;
        Assert.Equal(CombatBattleHostResult.Unconfirmed,await host.AdvanceAsync(flow,default));
        Assert.Equal(inputs,io.Inputs.Count);Assert.Equal(attacks,game.Inputs);Assert.Equal(waits,io.DelayCalls);
        Assert.Equal(24,host.CameraRequests);Assert.Equal(0,host.ApproachRequests);
        Assert.DoesNotContain(io.Traces,trace=>trace.Reason=="target-returned-after-party-probe");
    }

    private static CombatBattleObservation EdgeHintObservation(CaptureFrameStamp source,Guid battle,FakeTimeProvider clock,
        UnconfirmedSearchHintKind kind=UnconfirmedSearchHintKind.ClippedHealthBar,string fault="",EnemySeekDecision? target=null)
    {
        var hint=new UnconfirmedSearchHint(fault=="hint-source"?default:source,new(0,668,23,5,100),fault=="dims"?960:1920,1080,kind);
        var snapshot=new BetterGenshinImpact.GameTask.AutoFight.Model.PassiveTargetObservation(false,false,clock.GetUtcNow().UtcDateTime,null,1920,1080,null)
        { Source=source,BattleId=fault=="foreign"?Guid.NewGuid():battle,CaptureEpoch=10,Quality=CombatObservationQuality.Available,
          SearchHint=hint,Control=new(MotionStatus.Unknown,fault=="control") };
        var projected=NativeCombatBattleHostIo.ProjectObservation(snapshot,fault=="epoch"?11:10,true,clock.GetUtcNow().UtcDateTime);
        return projected with { Target=target,Source=fault=="unknown"?default:fault=="late"?source with {CapturedTimestamp=source.CapturedTimestamp-clock.TimestampFrequency}:source };
    }

    [Fact]
    public async Task CurrentClippedHealthHintUsesOneHorizontalCameraBeforeTheFirstReset()
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId);
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var before=io.Inputs.Count;
        io.TargetFactory=stamp=>EdgeHintObservation(stamp,flow.Context.BattleId,clock);
        for(var step=0;step<200&&!io.Inputs.Skip(before).Any();step++) await host.AdvanceAsync(flow,default);
        var sent=Assert.Single(io.Inputs.Skip(before));
        Assert.Equal(CombatBattleHostInputKind.Camera,sent.Kind); Assert.Equal(-120,sent.X); Assert.Equal(0,sent.Y);
        Assert.Equal(1,host.CameraRequests); Assert.Equal(0,host.ApproachRequests);
        io.TargetFactory=stamp=>new(stamp,flow.Context.BattleId,CombatObservationQuality.Available,null,1920,1080) {Control=new(MotionStatus.Unknown,false)};
        for(var step=0;step<200&&!io.Inputs.Any(input=>input.Kind==CombatBattleHostInputKind.CameraReset);step++) await host.AdvanceAsync(flow,default);
        Assert.Single(io.Inputs.Where(input=>input.Kind==CombatBattleHostInputKind.CameraReset));
    }

    [Theory]
    [InlineData("body")]
    [InlineData("direction")]
    public async Task CurrentConfirmedTargetWinsOverEdgeHint(string kind)
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId); using var host=new CombatBattleHost(io,new() {FinishCheckIntervalSeconds=.1});
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var target=AutoFightSeek.SelectSeekDecision([kind=="body"?new EnemySeekVisual(1400,400,80,6,480):new EnemySeekVisual(1372,731,30,26,433,128.916d)],1920,1080);
        io.TargetFactory=stamp=>EdgeHintObservation(stamp,flow.Context.BattleId,clock,target:target);
        for(var step=0;step<200&&!io.Inputs.Any(input=>input.Kind==CombatBattleHostInputKind.Camera);step++) await host.AdvanceAsync(flow,default);
        Assert.Equal(120,Assert.Single(io.Inputs.Where(input=>input.Kind==CombatBattleHostInputKind.Camera)).X);
    }

    [Fact]
    public async Task DirectionHintKeepsItsOriginalResetAndVerticalOrbit()
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId); using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var before=io.Inputs.Count;
        io.TargetFactory=stamp=>EdgeHintObservation(stamp,flow.Context.BattleId,clock,UnconfirmedSearchHintKind.DirectionIndicator);
        for(var step=0;step<200&&!io.Inputs.Skip(before).Any(input=>input.Kind==CombatBattleHostInputKind.Camera);step++) await host.AdvanceAsync(flow,default);
        Assert.Equal(CombatBattleHostInputKind.CameraReset,io.Inputs[before].Kind);
        Assert.Contains(io.Inputs.Skip(before),input=>input.Kind==CombatBattleHostInputKind.Camera&&input.Y!=0);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("foreign")]
    [InlineData("epoch")]
    [InlineData("late")]
    [InlineData("control")]
    [InlineData("hint-source")]
    [InlineData("dims")]
    public async Task InvalidEdgeHintCannotAcquireTheClippedCameraPath(string fault)
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId); using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        io.TargetFactory=stamp=>EdgeHintObservation(stamp,flow.Context.BattleId,clock,fault:fault);
        var result=CombatBattleHostResult.Continue;
        for(var step=0;step<1500&&result==CombatBattleHostResult.Continue;step++) result=await host.AdvanceAsync(flow,default);
        Assert.DoesNotContain(io.Traces,trace=>trace.Reason=="scan-clipped-health-hint");
        Assert.Equal(0,host.ApproachRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EdgeHintDisappearanceOrKindChangeCancelsItsUnsentRequest(bool changeKind)
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId); using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var prepared=new List<CombatBattleHostInput>();var cancellations=0;
        io.PrepareEvidence=input=>{if(input.Kind==CombatBattleHostInputKind.Camera)prepared.Add(input);return true;};
        io.CancelEvidence=()=>cancellations++;
        io.TargetFactory=stamp=>EdgeHintObservation(stamp,flow.Context.BattleId,clock);
        for(var step=0;step<200&&prepared.Count==0;step++) await host.AdvanceAsync(flow,default);
        var pending=Assert.Single(prepared);var before=cancellations;
        io.TargetFactory=stamp=>changeKind?EdgeHintObservation(stamp,flow.Context.BattleId,clock,UnconfirmedSearchHintKind.DirectionIndicator)
            :new(stamp,flow.Context.BattleId,CombatObservationQuality.Available,null,1920,1080){Control=new(MotionStatus.Unknown,false)};
        await host.AdvanceAsync(flow,default);
        Assert.True(cancellations>before);Assert.DoesNotContain(io.Inputs,input=>input.RequestId==pending.RequestId);
    }

    [Fact]
    public async Task PersistentClippedHintConsumesOnlyTheOriginalSearchBudgetAndNeverCompletes()
    {
        var clock=new FakeTimeProvider();using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId);using var host=new CombatBattleHost(io,new(){FinishCheckIntervalSeconds=.1});
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        io.TargetFactory=stamp=>EdgeHintObservation(stamp,flow.Context.BattleId,clock);
        var result=CombatBattleHostResult.Continue;
        for(var step=0;step<5000&&result==CombatBattleHostResult.Continue;step++)result=await host.AdvanceAsync(flow,default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed,result);Assert.Equal(24,host.CameraRequests);
        Assert.All(io.Inputs.Where(input=>input.Kind==CombatBattleHostInputKind.Camera),input=>{Assert.Equal(-120,input.X);Assert.Equal(0,input.Y);});
        Assert.Equal(0,host.ApproachRequests);Assert.InRange(io.Traces.Last().ProgressAge,0,45);
    }

    [Theory]
    [InlineData("left")]
    [InlineData("right")]
    [InlineData("behind")]
    public async Task ExternalAuthorityRetainsItsOriginalPathAfterTwentyFourDirectionCameras(string direction)
    {
        var clock=new FakeTimeProvider(); var game=new ReturningGame(clock);
        using var flow=CreateFlow(false,game,clock);
        var visual=direction=="left"?new EnemySeekVisual(532,748,30,25,411,-125d)
            :direction=="right"?new EnemySeekVisual(1372,731,30,26,433,128.91686957617605)
            :new EnemySeekVisual(901,923,38,31,428,-180d);
        var selected=AutoFightSeek.SelectSeekDecision([visual],1920,1080);
        Assert.Equal(SeekCueKind.DirectionIndicator,selected.Cue);
        Assert.Equal(direction=="left"?EnemyIndicatorDirection.Left:direction=="right"?EnemyIndicatorDirection.Right:EnemyIndicatorDirection.Behind,selected.Direction);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,selected,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { ExternalCompletionAuthority=true,TimeoutSeconds=120 });
        var result=CombatBattleHostResult.Continue;
        for(var step=0;step<10000&&result==CombatBattleHostResult.Continue&&host.CameraRequests<24;step++)
            result=await host.AdvanceAsync(flow,default);
        Assert.Equal(24,io.Inputs.Count(input=>input.Kind==CombatBattleHostInputKind.Camera));
        for(var step=0;step<30&&result==CombatBattleHostResult.Continue;step++) result=await host.AdvanceAsync(flow,default);
        Assert.Equal(CombatBattleHostResult.Continue,result);
        Assert.NotEqual("engagement-camera-budget-exhausted",host.Reason);
        Assert.Equal(24,host.CameraRequests);
        Assert.Equal(24,io.Inputs.Count(input=>input.Kind==CombatBattleHostInputKind.Camera));
        Assert.DoesNotContain(io.Inputs,input=>input.Kind==CombatBattleHostInputKind.Approach);
        Assert.Contains(io.Traces,trace=>trace.Reason is "bounded-reengagement" or "external-scene-awaiting-authority-after-bounded-search");
    }

    [Theory]
    [InlineData(200)]
    [InlineData(865)]
    public async Task UnalignedSpatialBodyExhaustsCameraBudgetWithoutBorrowingApproach(int y)
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        Assert.Equal("Searching",host.State);
        var visual=new EnemySeekVisual(946,y,28,4,112);
        var selected=AutoFightSeek.SelectSeekDecision([visual],1920,1080);
        Assert.Equal(SeekCueKind.HealthBar,selected.Cue);
        Assert.Equal(AutoFightSeekAction.ApproachVisibleEnemy,selected.Action);
        Assert.Equal(0,AutoFightSeek.GetVisibleEnemyCameraOffset(visual,1920));
        var requiredY=AutoFightSeek.GetVisibleEnemyCameraVerticalOffset(visual,1080);
        Assert.NotEqual(0,requiredY);
        io.TargetFactory=stamp=>new(stamp,flow.Context.BattleId,CombatObservationQuality.Available,selected,1920,1080)
            { Control=new(MotionStatus.Unknown,false) };
        var result=CombatBattleHostResult.Continue;
        for(var step=0;step<10000&&result==CombatBattleHostResult.Continue;step++) result=await host.AdvanceAsync(flow,default);
        var cameras=io.Inputs.Where(input=>input.Kind==CombatBattleHostInputKind.Camera).ToArray();
        Assert.Equal(24,cameras.Length); Assert.Equal(24,host.CameraRequests);
        Assert.All(cameras,input=> { Assert.Equal(0,input.X); Assert.Equal(requiredY,input.Y); });
        Assert.DoesNotContain(io.Inputs,input=>input.Kind==CombatBattleHostInputKind.Approach);
        Assert.Equal(CombatBattleHostResult.Unconfirmed,result);
        Assert.InRange(io.Traces.Last().ProgressAge,0,45);
    }

    [Theory]
    [InlineData(false,"horizontal")]
    [InlineData(true,"horizontal")]
    [InlineData(false,"vertical-high")]
    [InlineData(true,"vertical-high")]
    [InlineData(false,"vertical-low")]
    [InlineData(true,"vertical-low")]
    public async Task CurrentBodyCenteringKeepsZeroAxesAndCanSendAVerticalOnlyCamera(bool searching,string position)
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        if(searching) for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var visual=position=="horizontal"?new EnemySeekVisual(1403,375,65,6,390)
            // Centered high targets must stay below the existing ambiguous top-HUD band.
            :new EnemySeekVisual(928,position=="vertical-high"?200:865,64,6,384);
        var selected=AutoFightSeek.SelectSeekDecision([visual],1920,1080);
        Assert.Equal(SeekCueKind.HealthBar,selected.Cue);
        io.TargetFactory=stamp=>new(stamp,flow.Context.BattleId,CombatObservationQuality.Available,selected,1920,1080)
            { Control=new(MotionStatus.Unknown,false) };
        for(var step=0;step<2000&&!io.Inputs.Any(input=>input.Kind==CombatBattleHostInputKind.Camera);step++) await host.AdvanceAsync(flow,default);
        var camera=Assert.Single(io.Inputs.Where(input=>input.Kind==CombatBattleHostInputKind.Camera));
        if(position=="horizontal") { Assert.Equal(120,camera.X); Assert.Equal(0,camera.Y); }
        else { Assert.Equal(0,camera.X); Assert.Equal(AutoFightSeek.GetVisibleEnemyCameraVerticalOffset(visual,1080),camera.Y); }
        Assert.Equal(1,host.CameraRequests); Assert.Equal(0,host.ApproachRequests);
    }

    [Theory]
    [InlineData("direction")]
    [InlineData("fixed")]
    public async Task NonSpatialCuesKeepTheirOriginalCameraPath(string cue)
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var selected=cue=="direction"?AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(1372,731,30,26,433,128.916d)],1920,1080)
            :new EnemySeekDecision(AutoFightSeekAction.KeepFighting,EnemyIndicatorDirection.None,new(800,10,320,6,1800),1,SeekCueKind.FixedTopHealth);
        io.TargetFactory=stamp=>new(stamp,flow.Context.BattleId,CombatObservationQuality.Available,selected,1920,1080)
            { Control=new(MotionStatus.Unknown,false) };
        var before=io.Inputs.Count;
        for(var step=0;step<2000&&!io.Inputs.Skip(before).Any(input=>input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset);step++)
            await host.AdvanceAsync(flow,default);
        var first=io.Inputs.Skip(before).First(input=>input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset);
        if(cue=="direction") { Assert.Equal(CombatBattleHostInputKind.Camera,first.Kind); Assert.Equal(0,first.Y); }
        else Assert.Equal(CombatBattleHostInputKind.CameraReset,first.Kind);
        Assert.Equal(0,host.ApproachRequests);
    }

    [Theory]
    [InlineData("multi")]
    [InlineData("foreign")]
    [InlineData("control")]
    public async Task TwoAxisCameraCannotBorrowBodyEvidenceAcrossTheOriginalAdmissionGates(string fault)
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var selected=AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(403,67,64,6,384)],1920,1080);
        if(fault=="multi") selected=selected with { SignalCount=2 };
        var before=io.Inputs.Count;
        io.TargetFactory=stamp=>new(stamp,fault=="foreign"?Guid.NewGuid():flow.Context.BattleId,
            CombatObservationQuality.Available,selected,1920,1080) { Control=new(MotionStatus.Unknown,fault=="control") };
        var result=CombatBattleHostResult.Continue;
        for(var step=0;step<2000&&result==CombatBattleHostResult.Continue;step++) result=await host.AdvanceAsync(flow,default);
        Assert.DoesNotContain(io.Inputs.Skip(before),input=>input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.Approach);
    }

    [Fact]
    public async Task ADisappearingBodyCancelsItsUnsentVerticalCameraWithoutFollowingTheOldY()
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var visual=new EnemySeekVisual(403,67,64,6,384);
        var selected=AutoFightSeek.SelectSeekDecision([visual],1920,1080);
        var attempts=new List<CombatBattleHostInput>(); var cancelled=0;
        io.PrepareEvidence=input=> { if(input.Kind==CombatBattleHostInputKind.Camera) attempts.Add(input); return true; };
        io.CancelEvidence=()=>cancelled++;
        io.TargetFactory=stamp=>new(stamp,flow.Context.BattleId,CombatObservationQuality.Available,selected,1920,1080)
            { Control=new(MotionStatus.Unknown,false) };
        for(var step=0;step<2000&&attempts.Count==0;step++) await host.AdvanceAsync(flow,default);
        var reserved=Assert.Single(attempts); Assert.DoesNotContain(io.Inputs,input=>input.RequestId==reserved.RequestId);
        var oldCancels=cancelled;
        io.TargetFactory=stamp=>new(stamp,flow.Context.BattleId,CombatObservationQuality.Available,null,1920,1080)
            { Control=new(MotionStatus.Unknown,false) };
        for(var step=0;step<12;step++) await host.AdvanceAsync(flow,default);
        Assert.True(cancelled>oldCancels);
        Assert.DoesNotContain(io.Inputs,input=>input.RequestId==reserved.RequestId);
        Assert.DoesNotContain(io.Inputs,input=>input.Kind==CombatBattleHostInputKind.Camera&&input.Y==AutoFightSeek.GetVisibleEnemyCameraVerticalOffset(visual,1080));
    }


    [Theory]
    [InlineData(false,403,67,64,6)]
    [InlineData(false,1403,865,65,6)]
    [InlineData(true,403,67,64,6)]
    [InlineData(true,1403,865,65,6)]
    public async Task CurrentSpatialHealthUsesTheOriginalHorizontalAndVerticalCameraSteps(bool searching,int x,int y,int width,int height)
    {
        var clock=new FakeTimeProvider();
        using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId);
        io.TargetFactory=stamp=>new(stamp,flow.Context.BattleId,CombatObservationQuality.Available,null,1920,1080)
            { Control=new(MotionStatus.Unknown,false) };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        if(searching)
        {
            for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
            Assert.Equal("Searching",host.State);
        }
        var visual=new EnemySeekVisual(x,y,width,height,width*height);
        var selected=AutoFightSeek.SelectSeekDecision([visual],1920,1080);
        Assert.Equal(SeekCueKind.HealthBar,selected.Cue);
        io.TargetFactory=stamp=>new(stamp,flow.Context.BattleId,CombatObservationQuality.Available,selected,1920,1080)
            { Control=new(MotionStatus.Unknown,false) };
        var preparations=new List<CombatBattleHostInput>();
        io.PrepareEvidence=input=> { if(input.Kind==CombatBattleHostInputKind.Camera) preparations.Add(input); return true; };
        for(var step=0;step<2000&&!io.Inputs.Any(input=>input.Kind==CombatBattleHostInputKind.Camera);step++)
            await host.AdvanceAsync(flow,default);
        var sent=Assert.Single(io.Inputs.Where(input=>input.Kind==CombatBattleHostInputKind.Camera));
        Assert.True(preparations.Count>=2); // Original business source recheck, not diagnostic admission.
        Assert.Equal(preparations[0].RequestId,sent.RequestId);
        Assert.True(sent.Source.IsAfter(preparations[0].Source));
        Assert.True(io.Traces.Any(trace=>trace.State==(searching?"Searching":"Fighting")));
        Assert.Equal(Math.Clamp(AutoFightSeek.GetVisibleEnemyCameraOffset(visual,1920),-120,120),sent.X);
        var expectedY=AutoFightSeek.GetVisibleEnemyCameraVerticalOffset(visual,1080);
        Assert.NotEqual(0,expectedY); Assert.Equal(expectedY,sent.Y);
        Assert.Equal(0,host.ApproachRequests);
    }

    private static CombatBattleObservation PublishDirectionWorkingState(CaptureFrameStamp source, Guid battle,
        FakeTimeProvider clock, bool pending = true, EnemySeekDecision? established = null, bool wrongEpoch = false)
    {
        var gate = BetterGenshinImpact.GameTask.AutoFight.Model.AvatarRecognition.PassiveCaptureGate;
        BetterGenshinImpact.GameTask.AutoFight.Model.PassiveTargetObservation? published = null;
        Assert.True(BetterGenshinImpact.GameTask.AutoFight.Model.AvatarRecognition.PublishPassiveObservation(
            established?.Cue == SeekCueKind.HealthBar,false,established?.Visual,1920,1080,
            clock.GetUtcNow().UtcDateTime,gate.Epoch,indicatorDecision:established?.Cue==SeekCueKind.DirectionIndicator?established:null,
            source:source,battleId:battle,quality:CombatObservationQuality.Available,control:new(MotionStatus.Unknown,false),
            absenceReason:pending?"indicator-awaiting-stability":"all-visual-candidates-filtered",
            evidence:value=>published=value,directionConfirmationPending:pending));
        Assert.NotNull(published);
        return NativeCombatBattleHostIo.ProjectObservation(published.Value,gate.Epoch+(wrongEpoch?1:0),gate.CanCapture,clock.GetUtcNow().UtcDateTime);
    }

    [Fact]
    public async Task PermanentPendingStillStopsAtTheOriginalFortyFiveSecondNoProgressDeadline()
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var before=io.Inputs.Count;
        io.TargetFactory=stamp=>PublishDirectionWorkingState(stamp,flow.Context.BattleId,clock);
        var result=CombatBattleHostResult.Continue;
        for(var step=0;step<2000&&result==CombatBattleHostResult.Continue;step++) result=await host.AdvanceAsync(flow,default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed,result);
        Assert.InRange(io.Traces.Last().ProgressAge,45,45.051);
        Assert.Empty(io.Inputs.Skip(before));
        Assert.Equal(0,host.CameraRequests); Assert.Equal(0,host.ApproachRequests);
    }

    [Fact]
    public async Task DisappearingPendingReturnsToTheOriginalUnspentSearch()
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var pending=true;
        io.TargetFactory=stamp=>PublishDirectionWorkingState(stamp,flow.Context.BattleId,clock,pending);
        for(var step=0;step<8;step++) await host.AdvanceAsync(flow,default);
        Assert.Equal(0,host.CameraRequests);
        pending=false;
        for(var step=0;step<200&&host.CameraRequests==0;step++) await host.AdvanceAsync(flow,default);
        Assert.InRange(host.CameraRequests,1,24);
        Assert.Equal(0,host.ApproachRequests);
    }

    [Theory]
    [InlineData("body")]
    [InlineData("direction")]
    public async Task CurrentEstablishedTargetWinsOverPendingMetadata(string kind)
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var selected=AutoFightSeek.SelectSeekDecision([kind=="body"?new EnemySeekVisual(1400,400,80,4,300)
            :new EnemySeekVisual(1372,731,32,29,413,128.916d)],1920,1080);
        io.TargetFactory=stamp=>PublishDirectionWorkingState(stamp,flow.Context.BattleId,clock,true,selected);
        for(var step=0;step<200&&host.CameraRequests==0;step++) await host.AdvanceAsync(flow,default);
        Assert.True(host.CameraRequests>0);
    }

    [Fact]
    public async Task NewlyPublishedPendingCancelsAnUnsentGenericCameraReservation()
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        var prepared=0; var cancelled=0;
        CombatBattleHostInput? reserved=null;
        io.PrepareEvidence=input=>
        {
            if(input.Kind==CombatBattleHostInputKind.Camera) { prepared++; reserved=input; }
            return true;
        };
        io.CancelEvidence=()=>cancelled++;
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&prepared==0;step++) await host.AdvanceAsync(flow,default);
        Assert.Equal(1,prepared); Assert.True(reserved.HasValue);
        Assert.Equal("Searching",host.State);
        Assert.Equal("awaiting-business-decision-frame",host.Reason);
        var held=reserved.Value;
        Assert.Equal(CombatBattleHostInputKind.Camera,held.Kind);
        Assert.NotEqual(Guid.Empty,held.RequestId); Assert.True(held.Source.IsKnown);
        Assert.DoesNotContain(io.Inputs,sent=>sent.RequestId==held.RequestId);
        var before=io.Inputs.Count; var cancelsBefore=cancelled;
        io.TargetFactory=stamp=>PublishDirectionWorkingState(stamp,flow.Context.BattleId,clock);
        for(var step=0;step<10;step++) await host.AdvanceAsync(flow,default);
        Assert.True(cancelled>cancelsBefore);
        Assert.Equal(1,prepared); Assert.Empty(io.Inputs.Skip(before));
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("foreign")]
    [InlineData("epoch")]
    [InlineData("control")]
    [InlineData("unknown-source")]
    public async Task PendingDoesNotAuthorizeInputsAcrossTheOriginalSourceAndControlGates(string fault)
    {
        var clock=new FakeTimeProvider(); using var flow=CreateFlow(false,new ReturningGame(clock),clock);
        var io=new ReplayIo(clock,flow.Context.BattleId) { TargetFactory=stamp=>new(stamp,flow.Context.BattleId,
            CombatObservationQuality.Available,null,1920,1080) { Control=new(MotionStatus.Unknown,false) } };
        using var host=new CombatBattleHost(io,new() { FinishCheckIntervalSeconds=.1 });
        for(var step=0;step<2000&&host.State!="Searching";step++) await host.AdvanceAsync(flow,default);
        var before=io.Inputs.Count;
        io.TargetFactory=stamp=>
        {
            var projected=PublishDirectionWorkingState(stamp,flow.Context.BattleId,clock,wrongEpoch:fault=="epoch");
            return projected with {
                Source=fault=="stale"?stamp with { CapturedTimestamp=stamp.CapturedTimestamp-clock.TimestampFrequency }
                    :fault=="unknown-source"?default:stamp,
                BattleId=fault=="foreign"?Guid.NewGuid():flow.Context.BattleId,
                Control=new(MotionStatus.Unknown,fault=="control") };
        };
        var result=CombatBattleHostResult.Continue;
        for(var step=0;step<2000&&result==CombatBattleHostResult.Continue;step++) result=await host.AdvanceAsync(flow,default);
        Assert.DoesNotContain(io.Inputs.Skip(before),input=>input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset or CombatBattleHostInputKind.Approach);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishedDirectionAwaitingStabilityCannotBeSpentAsAnEmptySearchFrame(bool hint)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId)
        {
            TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
                { Control = new(MotionStatus.Unknown, false) }
        };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var step = 0; step < 2000 && host.State != "Searching"; step++) await host.AdvanceAsync(flow, default);
        Assert.Equal("Searching", host.State);
        clock.Advance(TimeSpan.FromSeconds(1)); // Original settle has elapsed; no deadline is extended.
        var before = io.Inputs.Count;
        // Saved S37/1141 CV packet (image SHA928A537A…): replay typed data, not model inference.
        var rawPacket = AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(1372,731,30,26,433,128.91686957617605)],1920,1080);
        Assert.Equal(SeekCueKind.DirectionIndicator,rawPacket.Cue);
        Assert.Equal(EnemyIndicatorDirection.Right,rawPacket.Direction);
        io.TargetFactory = stamp =>
        {
            // The original ReplayIo owns the fresh test source, unchanged between the old and new interfaces.
            // S37 source1141 packet: original CV accepted Right; publication still awaits the original stability gate.
            // This replays the published working state, not a simulation of the 120ms algorithm.
            var epoch = BetterGenshinImpact.GameTask.AutoFight.Model.AvatarRecognition.PassiveCaptureGate;
            BetterGenshinImpact.GameTask.AutoFight.Model.PassiveTargetObservation? published = null;
            Assert.True(BetterGenshinImpact.GameTask.AutoFight.Model.AvatarRecognition.PublishPassiveObservation(
                false, false, null, 1920, 1080, clock.GetUtcNow().UtcDateTime, epoch.Epoch,
                source: stamp, battleId: flow.Context.BattleId, quality: CombatObservationQuality.Available,
                control: new(MotionStatus.Unknown, false), absenceReason: "indicator-awaiting-stability",
                searchHint: hint ? new(stamp,new EnemySeekVisual(1372,731,32,29,413),1920,1080) : null,
                evidence: value => published = value, directionConfirmationPending: true));
            Assert.NotNull(published);
            Assert.Equal("indicator-awaiting-stability", published!.Value.TargetAbsenceReason);
            return NativeCombatBattleHostIo.ProjectObservation(published.Value,epoch.Epoch,epoch.CanCapture,clock.GetUtcNow().UtcDateTime);
        };
        for (var step = 0; step < 5; step++) await host.AdvanceAsync(flow, default);
        Assert.DoesNotContain(io.Inputs.Skip(before), input => input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset or CombatBattleHostInputKind.Approach);
    }

    [Fact]
    public async Task RetainedSearchDirectionStillExhaustsTwentyFourSlotsWithoutCompletionEvidence()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId)
        {
            TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
                { Control = new(MotionStatus.Unknown, false) }
        };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var step = 0; step < 2000 && host.State != "Searching"; step++) await host.AdvanceAsync(flow, default);
        var firstDirection = AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(532,748,30,25,411,-125d)],1920,1080);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            io.Inputs.Count(input => input.Kind == CombatBattleHostInputKind.Camera) == 0 ? firstDirection : null, 1920, 1080)
            { Control = new(MotionStatus.Unknown, false) };
        var result = CombatBattleHostResult.Continue;
        for (var step = 0; step < 6000 && result == CombatBattleHostResult.Continue; step++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Equal(24, io.Traces.Max(trace => trace.ScanUsed));
        Assert.InRange(host.CameraRequests, 1, 24);
        Assert.Equal(0, host.ApproachRequests);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
    }

    [Fact]
    public async Task NoAcceptedDirectionPreservesTheOriginalGenericOrbitAndVerticalTrack()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId)
        {
            TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
                { Control = new(MotionStatus.Unknown, false) }
        };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var step = 0; step < 3000 && io.Inputs.Count(input => input.Kind == CombatBattleHostInputKind.Camera) < 4; step++)
            await host.AdvanceAsync(flow, default);
        Assert.Single(io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.CameraReset));
        var cameras = io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.Camera).Take(4).ToArray();
        Assert.Equal(4, cameras.Length);
        var previousVertical = 0;
        for (var index = 0; index < cameras.Length; index++)
        {
            var slot = index + 1; // Original reset uses the first slot.
            var original = AutoFightSeek.GetSeekCameraOffset(1920, 1080, slot / 4, slot % 4);
            var vertical = AutoFightSeek.GetSeekCameraVerticalTargetOffset(1080, slot / 4, slot % 4 + 1);
            Assert.Equal(original.x, cameras[index].X);
            Assert.Equal(vertical - previousVertical, cameras[index].Y);
            previousVertical = vertical;
        }
    }

    [Theory]
    [InlineData("wrong-battle")]
    [InlineData("stale")]
    [InlineData("unknown-source")]
    [InlineData("control-blocked")]
    public async Task RetainedDirectionCannotAuthorizeAnUnavailableGenericCameraFrame(string fault)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId)
        {
            TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
                { Control = new(MotionStatus.Unknown, false) }
        };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var step = 0; step < 2000 && host.State != "Searching"; step++) await host.AdvanceAsync(flow, default);
        var arrow = AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(532, 748, 30, 25, 411, -125d)], 1920, 1080);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            io.Inputs.Count(input => input.Kind == CombatBattleHostInputKind.Camera) == 0 ? arrow : null, 1920, 1080)
            { Control = new(MotionStatus.Unknown, false) };
        for (var step = 0; step < 200 && !io.Inputs.Any(input => input.Kind == CombatBattleHostInputKind.Camera); step++)
            await host.AdvanceAsync(flow, default);
        Assert.Single(io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.Camera));
        io.TargetFactory = stamp => new(fault == "unknown-source" ? default : fault == "stale"
            ? stamp with { CapturedTimestamp = stamp.CapturedTimestamp - clock.TimestampFrequency } : stamp,
            fault == "wrong-battle" ? Guid.NewGuid() : flow.Context.BattleId,
            CombatObservationQuality.Available, null, 1920, 1080)
            { Control = new(MotionStatus.Unknown, fault == "control-blocked") };
        var result = CombatBattleHostResult.Continue;
        for (var step = 0; step < 2000 && result == CombatBattleHostResult.Continue; step++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Single(io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.Camera));
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
    }

    [Fact]
    public async Task FreshOppositeDirectionAndCurrentBodyOverrideTheRetainedGenericSearchDirection()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId)
        {
            TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
                { Control = new(MotionStatus.Unknown, false) }
        };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var step = 0; step < 2000 && host.State != "Searching"; step++) await host.AdvanceAsync(flow, default);
        io.TargetFactory = stamp =>
        {
            var count = io.Inputs.Count(input => input.Kind == CombatBattleHostInputKind.Camera);
            EnemySeekDecision? target = count == 0 ? AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(532,748,30,25,411,-125d)],1920,1080)
                : count == 2 ? AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(1358,748,30,25,411,125d)],1920,1080)
                : count >= 4 ? AutoFightSeek.SelectSeekDecision([new EnemySeekVisual(350,400,80,4,300)],1920,1080) : null;
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, target, 1920, 1080)
                { Control = new(MotionStatus.Unknown, false) };
        };
        for (var step = 0; step < 4000 && io.Inputs.Count(input => input.Kind == CombatBattleHostInputKind.Camera) < 5; step++)
            await host.AdvanceAsync(flow, default);
        var cameras = io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.Camera).Take(5).ToArray();
        Assert.Equal(5, cameras.Length);
        Assert.True(cameras[0].X < 0); Assert.True(cameras[1].X < 0);
        Assert.True(cameras[2].X > 0); Assert.True(cameras[3].X > 0);
        Assert.True(cameras[4].X < 0); // Actual current spatial target wins over old direction memory.
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(1, false)]
    [InlineData(-1, true)]
    [InlineData(1, true)]
    public async Task ProducerDirectionSurvivesIntermittentAbsenceUntilTheBodyEntersTheSameBoundedSearch(int side, bool behind)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
            { Control = new(MotionStatus.Unknown, false) };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var step = 0; step < 2000 && host.State != "Searching"; step++) await host.AdvanceAsync(flow, default);
        Assert.Equal("Searching", host.State);
        var arrow = new EnemySeekVisual(side < 0 ? 532 : 1358, 748, 30, 25, 411, side * 125d);
        var body = new EnemySeekVisual(920, 400, 80, 4, 300);
        var bodySeen = false;
        io.TargetFactory = stamp =>
        {
            var cameras = io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.Camera).ToArray();
            // External relative-input projection; this is not a degrees calibration.
            var relative = cameras.Sum(input => input.X);
            bodySeen |= relative * side >= 2000;
            EnemySeekDecision? target = null;
            if (bodySeen || cameras.Length % 2 == 0)
            {
                var visual = bodySeen ? body : behind && cameras.Length > 0
                    ? arrow with { IndicatorBearingDegrees = 180d } : arrow;
                var selected = AutoFightSeek.SelectSeekDecision([visual], 1920, 1080);
                var passive = new BetterGenshinImpact.GameTask.AutoFight.Model.PassiveTargetObservation(
                    bodySeen, false, clock.GetUtcNow().UtcDateTime, visual, 1920, 1080, selected)
                    { Source = stamp, BattleId = flow.Context.BattleId };
                Assert.True(AutoFightSeek.TryCreatePassiveDecision(passive, clock.GetUtcNow().UtcDateTime, out var admitted, out _, out _));
                target = admitted;
            }
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, target, 1920, 1080)
                { Control = new(MotionStatus.Unknown, false) };
        };
        var result = CombatBattleHostResult.Continue;
        for (var step = 0; step < 6000 && result == CombatBattleHostResult.Continue && host.ApproachRequests == 0; step++)
            result = await host.AdvanceAsync(flow, default);
        Assert.True(bodySeen, "Repeated valid side evidence must not be undone by the no-target orbit before the original scan budget ends.");
        Assert.True(host.ApproachRequests > 0);
        Assert.InRange(host.CameraRequests, 1, 24);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Camera && input.X * side < 0);
    }

    [Theory]
    [InlineData("target")]
    [InlineData("control")]
    [InlineData("action")]
    public async Task ReservedCandidateChangeDiscardsOldLeaseWithoutSendingOrReusingItsDeadline(string change)
    {
        await using var scope = new BetterGenshinImpact.GameTask.Common.DiagnosticEvidenceScope((_, _) => Task.CompletedTask);
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var attempts = new List<CombatBattleHostInput>();
        BetterGenshinImpact.GameTask.Common.DiagnosticFrameReservation? reservation = null;
        BetterGenshinImpact.GameTask.Common.DiagnosticExactFrameLease? lease = null;
        io.PrepareEvidence = input =>
        {
            attempts.Add(input);
            if (attempts.Count != 1) return true;
            reservation = scope.TryReserveExactFrame(new(flow.Context.BattleId, input.RequestId, 1), input.Source,
                clock, input.DeadlineTimestamp, default);
            using var frame = new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(
                new OpenCvSharp.Mat(2, 2, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black), 0, 0)
            { FrameStamp = input.Source with { Sequence = input.Source.Sequence + 1 } };
            Assert.True(scope.TryFulfillExactFrame(reservation!, frame));
            lease = scope.TryAcquireExactFrame(reservation!);
            return false;
        };
        io.CancelEvidence = () => { lease?.Dispose(); reservation?.Dispose(); };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        try
        {
            for (var i = 0; i < 200 && attempts.Count == 0; i++) await host.AdvanceAsync(flow, default);
            var original = Assert.Single(attempts);
            io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
                change == "control" ? null : new(change == "action" ? AutoFightSeekAction.ApproachVisibleEnemy : AutoFightSeekAction.Scan,
                    EnemyIndicatorDirection.None, new(change == "action" ? 920 : 1400, 400, 80, 4, 320), 1, SeekCueKind.HealthBar), 1920, 1080)
            { Control = new(MotionStatus.Unknown, change == "control") };
            await host.AdvanceAsync(flow, default);
            Assert.Empty(io.Inputs);
            Assert.Single(attempts);
            Assert.Equal((0, 1L, 1L), scope.ExactReservationStatistics);
            io.TargetFactory = null;
            for (var i = 0; i < 200 && io.Inputs.Count == 0; i++) await host.AdvanceAsync(flow, default);
            var next = Assert.Single(io.Inputs);
            Assert.NotEqual(original.RequestId, next.RequestId);
            Assert.Equal(original.DeadlineTimestamp, attempts[0].DeadlineTimestamp);
            Assert.True(next.DeadlineTimestamp > original.DeadlineTimestamp);
        }
        finally { lease?.Dispose(); reservation?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CameraPerformanceRecheckRejectsOldOrForeignSessionFeedbackWithoutResending(bool foreign)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        CaptureFrameStamp submittedSource = default;
        io.ResultFactory = input =>
        {
            submittedSource = input.Source;
            clock.Advance(TimeSpan.FromMilliseconds(307));
            return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp())
            { NativeRequested = 1, NativeSubmitted = 1, PerformanceRecheck = new(input.RequestId, input.Source, input.DeadlineTimestamp, null) };
        };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 1500 && host.CameraRequests == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(1, host.CameraRequests);
        io.TargetFactory = stamp => new(foreign ? stamp with { SessionId = Guid.NewGuid() } : submittedSource,
            flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080);
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 400 && result == CombatBattleHostResult.Continue; i++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Single(io.Requests);
    }
    [Fact]
    public async Task ReservationRequestsBindTheShorterParentDeadlineBeforeWaiting()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 200 && host.State != "Searching"; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal("Searching", host.State);
        var pending = new List<CombatBattleHostInput>();
        io.PrepareEvidence = input =>
        {
            pending.Add(input);
            // No new business frame: diagnostics cannot grant admission or renew the parent.
            io.TargetFactory = _ => new(input.Source, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080);
            return false;
        };
        var start = clock.GetTimestamp();
        var failure = await Record.ExceptionAsync(() => BetterGenshinImpact.GameTask.Common.Ui.UiOperation.RunAsync("test-parent",
            TimeSpan.FromMilliseconds(100), default, async operation =>
            {
                for (var i = 0; i < 10; i++) await host.AdvanceAsync(flow, operation.Token);
                return true;
            }, clock: clock));
        Assert.NotNull(failure);
        Assert.NotEmpty(pending);
        Assert.All(pending, request => Assert.True(request.DeadlineTimestamp <= start + clock.TimestampFrequency / 10));
        Assert.Empty(io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.Camera));
    }
    [Fact]
    public async Task BusinessFrameRecheckKeepsOneRequestWithoutWaitingForDiagnosticReadiness()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var attempts = new List<CombatBattleHostInput>();
        io.PrepareEvidence = input =>
        {
            if (input.Kind != CombatBattleHostInputKind.Camera) return true;
            attempts.Add(input);
            return false;
        };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 200 && host.CameraRequests == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(2, attempts.Count);
        Assert.Single(attempts.Select(item => item.RequestId).Distinct());
        Assert.Single(attempts.Select(item => item.DeadlineTimestamp).Distinct());
        Assert.True(attempts[^1].Source.IsAfter(attempts[0].Source));
        Assert.Single(io.Inputs.Where(item => item.Kind == CombatBattleHostInputKind.Camera));
    }

    [Fact]
    public async Task CompleteCameraOverrunWaitsAtLeastSixtyMillisecondsForSameSourceFeedback()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        long completed = 0;
        io.ResultFactory = input =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(307));
            completed = clock.GetTimestamp();
            return new(CombatBattleHostInputStatus.Sent, completed)
            { NativeRequested = 1, NativeSubmitted = 1, ObservableAfterTimestamp = completed,
                PerformanceRecheck = new(input.RequestId, input.Source, input.DeadlineTimestamp, input.SelectionGoal) };
        };
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        // Without party probing, this fixture reaches search through its existing no-progress policy.
        for (var i = 0; i < 1500 && host.CameraRequests == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(1, host.CameraRequests);
        Assert.Equal("host-input-performance-awaiting-fresh-evidence", host.Reason);
        for (var i = 0; i < 10 && host.Reason != "host-input-performance-rechecked"; i++)
            await host.AdvanceAsync(flow, default);
        Assert.Equal("host-input-performance-rechecked", host.Reason);
        Assert.True(clock.GetElapsedTime(completed).TotalMilliseconds >= 60);
        Assert.Single(io.Requests);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DrainStepCannotStartAnIdleRootAndNormalStepsStillCan(bool json)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(json, game, clock);
        var idle = await flow.StepAsync(default, allowNewRound: false);
        Assert.Equal(CombatFlowResult.Deferred, idle.Result);
        Assert.Equal(0, game.Inputs);
        Assert.Equal(CombatFlowResult.Succeeded, await flow.RunRoundAsync(default));
        var inputs = game.Inputs;
        for (var i = 0; i < 3; i++) await flow.StepAsync(default, allowNewRound: false);
        Assert.Equal(inputs, game.Inputs);
        Assert.Equal(CombatFlowResult.Succeeded, await flow.RunRoundAsync(default));
        Assert.True(game.Inputs > inputs);
    }

    [Fact]
    public async Task JsonDrainingPreparationCannotStartTheNewlyEligiblePriorityRule()
    {
        var clock = new FakeTimeProvider();
        var game = new DrainPreparationGame();
        using var flow = NativeCombatFlowRunner.Create(new JsonCombatStrategy
        {
            Actions = [
                new() { Character = "琴", Action = "attack(0.1)", Condition = new() { Expression = "q-ready(琴)" } },
                new() { Character = "琴", Action = "attack(0.2)", Condition = new() { Expression = "e-ready(琴)" } }]
        }, game, clock: clock)!;
        Assert.Equal(CombatFlowResult.AwaitingObservation, (await flow.StepAsync(default)).Result);
        Assert.True(flow.HasAwaitingObservation);
        game.Release = true;
        await flow.StepAsync(default, allowNewRound: false);
        Assert.False(flow.HasAwaitingObservation);
        Assert.Equal(0, game.Inputs);
        await flow.StepAsync(default, allowNewRound: false);
        Assert.Equal(0, game.Inputs);
        Assert.Equal(CombatFlowResult.Succeeded, await flow.RunRoundAsync(default));
        Assert.Equal(1, game.Inputs);
    }

    private sealed class DrainPreparationGame : ICombatFlowGame
    {
        public bool Release { get; set; }
        public int Inputs { get; private set; }
        private bool _prepared;
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) =>
            _prepared ? true : function == "q-ready" ? false : null;
        public ValueTask<CombatObservationPreparation> PrepareObservationStepAsync(CombatFlowAction action, string function, CancellationToken ct)
        {
            if (!Release) return ValueTask.FromResult(CombatObservationPreparation.AwaitingObservation);
            _prepared = true;
            action.ReportActiveActor("琴");
            return ValueTask.FromResult(CombatObservationPreparation.Ready);
        }
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            if (action.TryBeginInput()) Inputs++;
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReengagementDeadlineDrainsExistingObservationWithoutStartingAnotherAction(bool json)
    {
        var clock = new FakeTimeProvider();
        var game = new LateObservationGame(clock);
        using var flow = CreateFlow(json, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 5, 400), 1, SeekCueKind.HealthBar), 1920, 1080);
        await host.AdvanceAsync(flow, default);
        io.TargetFactory = null;
        for (var i = 0; i < 1500 && host.State != "Reengaging"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Reengaging", host.State);
        clock.Advance(TimeSpan.FromSeconds(14.8));
        game.HoldObservation = true;
        for (var i = 0; i < 10 && !flow.HasAwaitingObservation; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.True(flow.HasAwaitingObservation);
        clock.Advance(TimeSpan.FromSeconds(.3));
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        game.HoldObservation = false;
        for (var i = 0; i < 10 && flow.HasAwaitingObservation; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.False(flow.HasAwaitingObservation);
        var inputs = game.Inputs;
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("BeforeParty", host.State);
        Assert.Equal(inputs, game.Inputs);
    }

    private sealed class LateObservationGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public bool HoldObservation { get; set; }
        public int Inputs { get; private set; }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => true;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (HoldObservation) return ValueTask.FromResult(CombatFlowResult.AwaitingObservation);
            action.TryBeginInput();
            Inputs++;
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReengagementCancellationOrObservationFailureReleasesInputImmediately(bool cancel)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 5, 400), 1, SeekCueKind.HealthBar), 1920, 1080);
        await host.AdvanceAsync(flow, default);
        io.TargetFactory = null;
        for (var i = 0; i < 1500 && host.State != "Reengaging"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Reengaging", host.State);
        using var cancellation = new CancellationTokenSource();
        if (cancel) cancellation.Cancel();
        else io.TargetFactory = _ => throw new IOException("recorded observation failed");
        var releaseCount = io.ReleaseCount;
        var inputCount = io.Inputs.Count;
        var error = await Record.ExceptionAsync(async () => await host.AdvanceAsync(flow, cancellation.Token));
        if (cancel) Assert.IsAssignableFrom<OperationCanceledException>(error);
        else Assert.IsType<IOException>(error);
        Assert.Equal(releaseCount + 1, io.ReleaseCount);
        Assert.Equal(inputCount, io.Inputs.Count);
    }

    [Fact]
    public async Task ReengagementObservationLossCannotRenewItsDeadline()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 5, 400), 1, SeekCueKind.HealthBar), 1920, 1080);
        await host.AdvanceAsync(flow, default);
        io.TargetFactory = null;
        for (var i = 0; i < 1500 && host.State != "Reengaging"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Reengaging", host.State);
        var started = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Unavailable, null, 1920, 1080);
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1000 && result == CombatBattleHostResult.Continue; i++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 15, 15.2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(28)]
    public async Task ReengagementWithoutNewProgressKeepsTheOriginalBudgetsAndStopsUnconfirmed(double initialDelay)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = NativeCombatFlowRunner.Create(
            CombatFlowProgram.Compile("strategy(loop=battle)\n琴 attack(0.1),check"), game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 5, 400), 1, SeekCueKind.HealthBar), 1920, 1080);
        await host.AdvanceAsync(flow, default);
        io.TargetFactory = null;
        clock.Advance(TimeSpan.FromSeconds(initialDelay));
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 5000 && result == CombatBattleHostResult.Continue; i++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        var reengagement = io.Traces.First(trace => trace.State == "Reengaging");
        var terminal = Assert.Single(io.Traces.Where(trace => trace.CapturePhase == "terminal"));
        Assert.True(terminal.ProgressAge >= Math.Min(reengagement.ProgressAge + 15, 45));
        Assert.True(terminal.ProgressAge < 47);
        Assert.Equal(24, host.CameraRequests);
        Assert.Equal(0, host.ApproachRequests);
        // 重进已抵达45秒时直接停止，不再额外发送一次编队探测。
        Assert.Equal(initialDelay == 0 ? 3 : 2, io.Inputs.Count(input => input.Kind == CombatBattleHostInputKind.OpenParty));
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.CloseParty);
        Assert.True(game.Inputs > 5);
    }

    [Theory]
    [InlineData("no-progress")]
    [InlineData("stale-party")]
    [InlineData("repeated-target")]
    [InlineData("foreign-battle")]
    public async Task ReengagementCannotStartWithoutTrustedEpisodeAndFreshEvidence(string invalid)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        if (invalid != "no-progress")
        {
            io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
                new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 5, 400), 1, SeekCueKind.HealthBar), 1920, 1080);
            await host.AdvanceAsync(flow, default);
            io.TargetFactory = null;
        }
        for (var i = 0; i < 1500 && !(host.CameraRequests == 24 && host.State == "CloseParty"); i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("CloseParty", host.State);
        var actions = game.Inputs;
        if (invalid == "stale-party") clock.Advance(TimeSpan.FromMilliseconds(151));
        if (invalid == "repeated-target")
        {
            var frozen = io.Traces.Last().Observation;
            io.TargetFactory = _ => frozen;
        }
        if (invalid == "foreign-battle")
            io.TargetFactory = stamp => new(stamp, Guid.NewGuid(), CombatObservationQuality.Available, null, 1920, 1080);
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 30 && result == CombatBattleHostResult.Continue; i++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Equal(actions, game.Inputs);
        Assert.Equal(24, host.CameraRequests);
        Assert.DoesNotContain(io.Traces, trace => trace.State == "Reengaging");
    }

    [Fact]
    public async Task RecentProgressAndTemporarilyHiddenTargetGetsOneStrategyOpportunityBeforeFinalFailure()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var game = new ReturningGame(clock);
        using var flow = NativeCombatFlowRunner.Create(
            CombatFlowProgram.Compile("strategy(loop=battle)\n琴 attack(0.1),check"), game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        io.TargetFactory = stamp =>
        {
            var at = clock.GetElapsedTime(started).TotalSeconds;
            EnemySeekDecision? target = at < .05
                ? new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 5, 400), 1, SeekCueKind.HealthBar)
                : at is >= 18 and < 18.4
                    ? new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber)
                    : null;
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, target, 1920, 1080, (ulong)stamp.Sequence);
        };
        io.PartyFactory = stamp => new(stamp.Sequence, stamp.CapturedAt, 1920, 1080,
            io.PartyOpen && clock.GetElapsedTime(started).TotalSeconds >= 18.4, (ulong)stamp.Sequence) { Source = stamp };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        var actionsAtSearchEnd = -1;
        for (var i = 0; i < 5000 && result == CombatBattleHostResult.Continue; i++)
        {
            result = await host.AdvanceAsync(flow, default);
            if (host.CameraRequests == 24 && actionsAtSearchEnd < 0) actionsAtSearchEnd = game.Inputs;
        }
        Assert.Equal(CombatBattleHostResult.Completed, result);
        Assert.True(actionsAtSearchEnd >= 0 && game.Inputs > actionsAtSearchEnd);
        Assert.Equal(24, host.CameraRequests);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 18.4, 30);
        Assert.Contains(io.Traces, trace => trace.State == "Reengaging");
        Assert.Contains(io.Inputs, input => input.Kind == CombatBattleHostInputKind.CloseParty);
        var finalTrace = Newtonsoft.Json.Linq.JObject.FromObject(io.Traces.Last());
        Assert.NotNull(finalTrace["LastProgress"]?["Source"]);
        Assert.NotNull(finalTrace["LastProgress"]?["Kind"]);
        Assert.True((long?)finalTrace["LastProgress"]?["Source"]?["Sequence"] > 0);
    }

    [Fact]
    public async Task FinalSearchMovementMustBeObservedAfterSettlingBeforeFinalPartyProbe()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 1500 && host.CameraRequests < 24; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal(24, host.CameraRequests);
        var lastMovement = clock.GetTimestamp();
        Assert.Equal("Searching", host.State);
        for (var i = 0; i < 50 && host.State == "Searching"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("BeforeParty", host.State);
        Assert.True(clock.GetElapsedTime(lastMovement) >= TimeSpan.FromMilliseconds(350));
        Assert.Equal(24, host.CameraRequests);
    }

    [Fact]
    public async Task SearchCameraActuallyVisitsHighMiddleAndLowTracks()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1500 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);

        var camera = io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.Camera).ToArray();
        Assert.Equal(24, camera.Length);
        var pitch = 0;
        var visited = camera.Select(input => pitch += input.Y).ToArray();
        // 1080p: 每个高度单位为432；首轮从高轨扫描，随后经过中、低轨。
        Assert.Equal(new[] { -1296, -864, -432, -864, -432, 0, 432, 0,
            432, 864, 1296, 864, -432, -864, -1296, -864,
            432, 0, -432, 0, 1296, 864, 432, 864 }, visited);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
    }

    [Fact]
    public async Task PersistentAmbiguousDirectionCannotStarveVerticalSearchOrExtendItsBudget()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId,
            CombatObservationQuality.Available, null, 1920, 1080)
        { SearchHint = new(stamp, new(527, 742, 30, 25, 425), 1920, 1080) };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1500 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        var camera = io.Inputs.Where(input => input.Kind == CombatBattleHostInputKind.Camera).ToArray();
        Assert.Equal(24, camera.Length);
        var pitch = 0;
        var visited = camera.Select(input => pitch += input.Y).ToArray();
        Assert.Contains(visited, value => value < 0);
        Assert.Contains(visited, value => value > 0);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TargetReturningAfterPartyInputResumesStrategyWithoutRestartingSearch(bool finalProbe)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        CombatBattleObservation Target(CaptureFrameStamp stamp) => new(stamp, flow.Context.BattleId,
            CombatObservationQuality.Available, new(AutoFightSeekAction.ApproachVisibleEnemy,
                EnemyIndicatorDirection.None, new(189, 417, 65, 7, 430), 1, SeekCueKind.HealthBar), 1920, 1080);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        io.TargetFactory = Target;
        await host.AdvanceAsync(flow, default); // 建立原有目标，不把重新出现当首次进展。
        io.TargetFactory = null;
        var expectedMenus = finalProbe ? 2 : 1;
        for (var i = 0; i < 1500 && io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.OpenParty) < expectedMenus; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal(expectedMenus, io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.OpenParty));
        var attacks = game.Inputs;
        var cameras = host.CameraRequests;
        io.TargetFactory = Target;
        for (var i = 0; i < 100 && game.Inputs == attacks; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.True(game.Inputs > attacks);
        Assert.Equal(cameras, host.CameraRequests);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.CloseParty);
        Assert.Contains(io.Traces, trace => trace.Reason == "target-returned-after-party-probe");
    }

    [Theory]
    [InlineData("stale")]
    [InlineData("other-battle")]
    [InlineData("other-session")]
    [InlineData("pre-input")]
    [InlineData("unknown-party")]
    public async Task FinalPartyProbeCannotResumeFromUntrustedTargetOrMissingPartySample(string invalid)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 1500 && !(host.CameraRequests == 24 && host.State == "CloseParty"); i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("CloseParty", host.State);
        var opened = io.Inputs.Last(x => x.Kind == CombatBattleHostInputKind.OpenParty);
        var attacks = game.Inputs;
        io.TargetFactory = stamp => new(invalid switch
        {
            "stale" => stamp with { CapturedTimestamp = stamp.CapturedTimestamp - clock.TimestampFrequency },
            "other-session" => stamp with { SessionId = Guid.NewGuid() },
            "pre-input" => opened.Source,
            _ => stamp
        }, invalid == "other-battle" ? Guid.NewGuid() : flow.Context.BattleId,
            CombatObservationQuality.Available, new(AutoFightSeekAction.ApproachVisibleEnemy,
                EnemyIndicatorDirection.None, new(189, 417, 65, 7, 430), 1, SeekCueKind.DirectionIndicator), 1920, 1080);
        // 缺少可用编队负证据时，即使目标本身新鲜也不恢复。
        if (invalid == "unknown-party") clock.Advance(TimeSpan.FromMilliseconds(151));
        Assert.Equal(CombatBattleHostResult.Unconfirmed, await host.AdvanceAsync(flow, default));
        Assert.Equal(attacks, game.Inputs);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.CloseParty);
    }

    [Fact]
    public async Task SearchFailureIncludesItsSourceBudgetAndTerminalEvidenceRequest()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1500 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Contains(io.Traces, trace => trace.CapturePhase == "search-start");
        var terminal = Assert.Single(io.Traces.Where(trace => trace.CapturePhase == "terminal"));
        Assert.Equal("bounded-search-finish-unconfirmed", terminal.Reason);
        Assert.Equal(24, terminal.ScanUsed);
        Assert.True(terminal.Observation.Source.IsKnown);
        Assert.Equal(flow.Context.BattleId, terminal.BattleId);
    }

    [Fact]
    public async Task RealProgressAfterAnExhaustedSearchDoesNotPoisonTheNextFinishProbe()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 1000 && !(host.CameraRequests == 24 && host.State == "BeforeParty"); i++)
            await host.AdvanceAsync(flow, default);
        Assert.Equal(24, host.CameraRequests);
        Assert.Equal("BeforeParty", host.State);
        clock.Advance(TimeSpan.FromMilliseconds(400));
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber), 1920, 1080, 1);
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Fighting", host.State);
        io.TargetFactory = null;
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 100 && result == CombatBattleHostResult.Continue && host.State != "Searching"; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Continue, result);
        Assert.Equal("Searching", host.State);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    public async Task SearchWaitsForTheCameraToSettleBeforeSendingAnotherMovement(int sourceLagMilliseconds)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        io.TargetFactory = stamp => new(stamp with
        { CapturedTimestamp = stamp.CapturedTimestamp - clock.TimestampFrequency * sourceLagMilliseconds / 1000 },
            flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080);
        for (var i = 0; i < 100 && host.CameraRequests == 0; i++)
            await host.AdvanceAsync(flow, default);
        Assert.Equal(1, host.CameraRequests);
        var completedFirstMovement = clock.GetTimestamp();
        for (var i = 0; i < 100 && host.CameraRequests == 1; i++)
            await host.AdvanceAsync(flow, default);
        Assert.Equal(2, host.CameraRequests);
        Assert.True(clock.GetElapsedTime(completedFirstMovement) >= TimeSpan.FromMilliseconds(350 + sourceLagMilliseconds),
            $"第二次镜头输入仅间隔{clock.GetElapsedTime(completedFirstMovement).TotalMilliseconds}ms，未给场景稳定机会");
    }

    [Fact]
    public void UnknownPostureMayPermitOnlyObservedAlignedBoundedMovement()
    {
        var clock = new FakeTimeProvider();
        var battle = Guid.NewGuid();
        var frame = new CombatBattleObservation(new CaptureFrameSource(clock).Next(), battle,
            CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None, new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar),
            1920, 1080) { Control = new(MotionStatus.Unknown, false) };
        Assert.True(CombatBattleHost.CanApproach(frame, battle, clock));
        Assert.Equal(MotionStatus.Unknown, frame.Motion);
        Assert.False(CombatBattleHost.CanApproach(frame with { Control = default }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Control = new(MotionStatus.Unknown, true) }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Control = new(MotionStatus.Climb, false) }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Control = new(MotionStatus.Fly, false) }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Target = null }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Target = frame.Target!.Value with { Cue = SeekCueKind.FixedTopHealth } }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Target = frame.Target!.Value with { Cue = SeekCueKind.DamageNumber } }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame with { Target = frame.Target!.Value with { Visual = new(100, 400, 100, 4, 400) } }, battle, clock));
        Assert.False(CombatBattleHost.CanApproach(frame, Guid.NewGuid(), clock));
        clock.Advance(TimeSpan.FromMilliseconds(151));
        Assert.False(CombatBattleHost.CanApproach(frame, battle, clock));
    }

    [Fact]
    public async Task ExhaustingCameraAttemptsCannotAuthorizeWalkingInAnUnconfirmedDirection()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(350, 400, 100, 4, 400), 1, SeekCueKind.HealthBar), 1920, 1080)
            { Motion = MotionStatus.Normal, Control = new(MotionStatus.Normal, false) };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.InRange(host.CameraRequests, 1, 24);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewEnemyEvidenceWithdrawsOnlyAnUnsentFinishProbe(bool alreadyPrepared)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 50 && host.State != "BeforeParty"; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal("BeforeParty", host.State);
        if (alreadyPrepared) { await host.AdvanceAsync(flow, default); Assert.Equal("OpenParty", host.State); }
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber), 1920, 1080, 1);
        for (var i = 0; i < 10; i++) await host.AdvanceAsync(flow, default);
        Assert.DoesNotContain(io.Requests, request => request.Kind == CombatBattleHostInputKind.OpenParty);
        Assert.Equal("Fighting", host.State);
    }

    [Fact]
    public async Task LateNativeObservationRejectsThisDecisionWithoutDestroyingTheBattle()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId)
        { AfterTargetCapture = () => { Thread.Sleep(180); clock.Advance(TimeSpan.FromMilliseconds(180)); } };
        using var host = new CombatBattleHost(io, new());
        Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal(0, game.Inputs);
        Assert.Empty(io.Requests);
        io.AfterTargetCapture = null;
        for (var i = 0; i < 20 && game.Inputs == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.True(game.Inputs > 0);
    }

    [Fact]
    public async Task AControlHintDuringSearchReturnsToTheKernelInsteadOfSendingAnotherCameraPulse()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishDetectionEnabled = false, FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 20 && host.State != "Searching"; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal("Searching", host.State);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, null, 1920, 1080)
        { Control = new(MotionStatus.Unknown, true) };
        var requests = io.Requests.Count;
        var kernelSteps = flow.RuntimeStatistics.CoreSteps;
        await host.AdvanceAsync(flow, default);
        Assert.Equal(requests, io.Requests.Count);
        Assert.True(flow.RuntimeStatistics.CoreSteps > kernelSteps);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContinuouslyUnsentRequestsKeepOneDeadlineEvenWithUnlimitedBattleTimeout(bool party)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { UnsentAttempts = int.MaxValue };
        using var host = new CombatBattleHost(io, new()
        { TimeoutSeconds = 0, FinishDetectionEnabled = party, FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Empty(io.Inputs);
        Assert.NotEmpty(io.Requests);
        Assert.Single(io.Requests.Select(x => x.RequestId).Distinct());
        Assert.Single(io.Requests.Select(x => x.DeadlineTimestamp).Distinct());
        Assert.Equal(0, host.CameraRequests);
        Assert.Equal(0, host.ApproachRequests);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 1, party ? 3 : 20);
    }

    [Fact]
    public async Task UnknownInputIsNotRetriedOrCountedAsSent()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId)
        { ForcedResult = new(CombatBattleHostInputStatus.Unknown, Reason: "partial-native-send") };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 50 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Single(io.Requests);
        Assert.Empty(io.Inputs);
        Assert.Equal(result, await host.AdvanceAsync(flow, default));
        Assert.Single(io.Requests);
    }

    [Fact]
    public async Task UnsentPartyRequestDoesNotEnterConfirmationAndCanContinueOnANewFrame()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { UnsentAttempts = 1 };
        io.PartyFactory = stamp => new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, io.PartyOpen, (ulong)stamp.Sequence)
        { Source = stamp };
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        for (var i = 0; i < 50 && io.Requests.Count == 0; i++) await host.AdvanceAsync(flow, default);
        Assert.Single(io.Requests);
        Assert.Empty(io.Inputs);
        Assert.Equal("BeforeParty", host.State);
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 100 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Completed, result);
        Assert.Single(io.Inputs, input => input.Kind == CombatBattleHostInputKind.OpenParty);
        Assert.NotEqual(Guid.Empty, io.Requests[0].RequestId);
        Assert.Equal(io.Requests[0].RequestId, io.Requests[1].RequestId);
        Assert.Equal(io.Requests[0].DeadlineTimestamp, io.Requests[1].DeadlineTimestamp);
        Assert.True(io.Requests[1].Source.IsAfter(io.Requests[0].Source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryIsDisabledByDefaultAndOnlyExplicitReplayAllowsBoundedDetach(bool enableReplayRecovery)
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar), 1920, 1080) { Motion = MotionStatus.Climb };
        using var host = new CombatBattleHost(io, new() { ControlRecoveryEnabled = enableReplayRecovery });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Equal(enableReplayRecovery ? 2 : 0, io.Inputs.Count(input => input.Kind == CombatBattleHostInputKind.Detach));
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
    }
    [Fact]
    public async Task UnknownPostureDoesNotAuthorizeWalkingIntoAnUnreachableTarget()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar), 1920, 1080) { Motion = MotionStatus.Unknown };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++) result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalSceneOwnsCompletionDuringABoundedSearchAndAnInvulnerablePhase(bool json)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(json, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { ExternalCompletionAuthority = true, TimeoutSeconds = 120 });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 10000 && clock.GetElapsedTime(started).TotalSeconds < 65 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Continue, result);
        Assert.True(game.Inputs > 0);
        Assert.DoesNotContain(io.Inputs, input => input.Kind is CombatBattleHostInputKind.OpenParty or CombatBattleHostInputKind.CloseParty);
        Assert.InRange(host.CameraRequests, 1, 24);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.AdvanceAsync(flow, cancelled.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulStrategyCallsCannotKeepATargetlessBattleAliveForever(bool json)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(json, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new CombatBattleHostOptions());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 5000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);

        Assert.True(game.Inputs > 0);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.Contains(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Camera);
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
        Assert.False(io.PartyOpen);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 1, 120);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHealthChangesKeepALongBattleRunningAcrossSuccessiveTargets(bool json)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(json, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp =>
        {
            var elapsed = clock.GetElapsedTime(started).TotalSeconds;
            var width = 240 - (int)(elapsed % 60) * 3;
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
                new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                    new(900, 400, width, 4, width * 4), 1, SeekCueKind.HealthBar), 1920, 1080);
        };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 25000 && clock.GetElapsedTime(started).TotalSeconds < 180 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Continue, result);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 180, 181);
        Assert.Empty(io.Inputs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedOrForeignSourceFramesCannotExtendObservationAvailability(bool foreignBattle)
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { RepeatSource = !foreignBattle };
        if (foreignBattle) io.TargetFactory = stamp => new(stamp, Guid.NewGuid(), CombatObservationQuality.Available, null, 1920, 1080);
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.InRange(game.Inputs, 0, 2); // 未过期帧可供纯调度复用，但不能续期或无界推进。
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task CancellationInThePartyWaitClosesTheOwnedUiBeforeReturning()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new());
        for (var i = 0; i < 1000 && !io.PartyOpen; i++) await host.AdvanceAsync(flow, default);
        Assert.True(io.PartyOpen);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await host.AdvanceAsync(flow, cts.Token));
        Assert.False(io.PartyOpen);
    }

    [Fact]
    public async Task AStationaryUnreachableTargetGetsOnlyBoundedApproachAndCannotBecomeCompleted()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar), 1920, 1080) { Control = new(MotionStatus.Unknown, false) };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.InRange(io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.Approach), 1, 12);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 1, 15);
        Assert.Equal("engagement-approach-budget-exhausted", host.Reason);
        Assert.False(io.PartyOpen);
    }

    [Fact]
    public async Task HealthBarGeometryChangedByOurOwnCameraCannotResetTheSearchBudget()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp =>
        {
            var width = 200 - (io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.Camera) % 50) * 2;
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
                new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                    new(400, 400, width, 4, width * 4), 1, SeekCueKind.HealthBar), 1920, 1080);
        };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.InRange(host.CameraRequests, 1, 24);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OnlyRedHealthReductionNotDarkTrackGeometryCountsAsBattleProgress(bool redDecreases)
        => await VerifyHealthProgress(redDecreases, false);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FixedBossProgressRemainsObservableAlongsideASpatialTarget(bool redDecreases)
        => await VerifyHealthProgress(redDecreases, true);

    private static async Task VerifyHealthProgress(bool redDecreases, bool fixedBoss)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp =>
        {
            var seconds = clock.GetElapsedTime(started).TotalSeconds;
            var redWidth = redDecreases ? Math.Max(8, 60 - 4 * (int)(seconds / 5)) : 14;
            var visual = new EnemySeekVisual(900, 400, redWidth, 6, redWidth * 6)
            { HealthBarTrackWidth = (int)(seconds % 2) == 0 ? 70 : 90 };
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
                new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                    fixedBoss ? new(900, 400, 60, 6, 360) : visual, 1, SeekCueKind.HealthBar), 1920, 1080)
            { FixedTopHealth = fixedBoss ? visual with { X = 700, Y = 20 } : null };
        };
        using var host = new CombatBattleHost(io, new() { SeekEnabled = false, FinishDetectionEnabled = false });
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 3000 && clock.GetElapsedTime(started).TotalSeconds < 60 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(redDecreases ? CombatBattleHostResult.Continue : CombatBattleHostResult.Unconfirmed, result);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, redDecreases ? 60 : 45, redDecreases ? 61 : 46);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task OnlyFreshIndependentPostInputPartyEvidenceCompletesAndClosesTheUi()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(true, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.PartyFactory = stamp => new(stamp.Sequence, stamp.CapturedAt, 1920, 1080,
            io.PartyOpen, (ulong)stamp.Sequence) { Source = stamp };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 1000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Completed, result);
        Assert.False(io.PartyOpen);
        Assert.Equal(1, io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.OpenParty));
        Assert.Equal(1, io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.CloseParty));
    }

    [Fact]
    public async Task StalePartyCandidatesNeitherCompleteNorAuthorizeASecondMenuToggle()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        CaptureFrameStamp before = default;
        io.PartyFactory = stamp =>
        {
            if (!io.PartyOpen) before = stamp;
            return new(before.Sequence, before.CapturedAt, 1920, 1080, io.PartyOpen, (ulong)stamp.Sequence) { Source = before };
        };
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 5000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.All(io.Inputs.Where(x => x.Kind == CombatBattleHostInputKind.CloseParty), input => Assert.False(input.PartyEvidence));
    }

    [Fact]
    public async Task AFixedTopBossBarNeverAuthorizesWalkingTowardAScreenCoordinate()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.ApproachFixedTopHealthTarget, EnemyIndicatorDirection.None,
                new(700, 20, 500, 8, 4000), 1, SeekCueKind.FixedTopHealth), 1920, 1080);
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 15000 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Unconfirmed, result);
        Assert.DoesNotContain(io.Inputs, x => x.Kind == CombatBattleHostInputKind.Approach);
        Assert.InRange(io.Inputs.Count(x => x.Kind == CombatBattleHostInputKind.Camera), 1, 24);
    }

    [Fact]
    public async Task ChangingDamageEvidenceSupportsLongCombatWithoutAVisibleHealthBar()
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None,
                new(700, 400, 80, 30, 2400), 1, SeekCueKind.DamageNumber), 1920, 1080,
            (ulong)(clock.GetElapsedTime(started).TotalSeconds / 2) + 1);
        using var host = new CombatBattleHost(io, new());
        var result = CombatBattleHostResult.Continue;
        for (var i = 0; i < 20000 && clock.GetElapsedTime(started).TotalSeconds < 120 && result == CombatBattleHostResult.Continue; i++)
            result = await host.AdvanceAsync(flow, default);
        Assert.Equal(CombatBattleHostResult.Continue, result);
        Assert.InRange(clock.GetElapsedTime(started).TotalSeconds, 120, 121);
        Assert.Empty(io.Inputs);
    }

    [Fact]
    public async Task FixedBossSearchHandsOffToTheNewSpatialTarget()
    {
        var clock = new FakeTimeProvider();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        var boss = new EnemySeekVisual(700, 20, 500, 8, 4000);
        io.TargetFactory = stamp => new(stamp, flow.Context.BattleId, CombatObservationQuality.Available,
            io.Inputs.Any(x => x.Kind == CombatBattleHostInputKind.Camera)
                ? new(AutoFightSeekAction.ApproachVisibleEnemy, EnemyIndicatorDirection.None,
                    new(910, 400, 100, 4, 400), 1, SeekCueKind.HealthBar)
                : new(AutoFightSeekAction.KeepFighting, EnemyIndicatorDirection.None, boss, 1, SeekCueKind.FixedTopHealth),
            1920, 1080) { FixedTopHealth = boss, Control = new(MotionStatus.Normal, false) };
        using var host = new CombatBattleHost(io, new());
        for (var i = 0; i < 15000 && !io.Inputs.Any(x => x.Kind == CombatBattleHostInputKind.Approach); i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Single(io.Inputs.Where(x => x.Kind == CombatBattleHostInputKind.Camera));
        Assert.Contains(io.Inputs, x => x.Kind == CombatBattleHostInputKind.Approach);
    }

    [Fact]
    public async Task FreshSharedObservationDoesNotThrottlePureStrategySteps()
    {
        var clock = new FakeTimeProvider();
        var game = new ReturningGame(clock);
        using var flow = CreateFlow(false, game, clock);
        var io = new ReplayIo(clock, flow.Context.BattleId) { SourcePeriodMilliseconds = 50 };
        using var host = new CombatBattleHost(io, new());
        for (var i = 0; i < 100 && game.Inputs < 10; i++) await host.AdvanceAsync(flow, default);
        Assert.Equal(10, game.Inputs);
        Assert.Equal(0, io.DelayCalls);
    }

    [Theory]
    [InlineData("handoff")]
    [InlineData("no-body")]
    [InlineData("wrong-source")]
    [InlineData("control-not-ready")]
    [InlineData("multiple")]
    public async Task CenteredProducerDirectionRetainsUnspentSearchForAScreenTarget(string scenario)
    {
        var clock = new FakeTimeProvider();
        var started = clock.GetTimestamp();
        using var flow = CreateFlow(false, new ReturningGame(clock), clock);
        var io = new ReplayIo(clock, flow.Context.BattleId);
        using var host = new CombatBattleHost(io, new() { FinishCheckIntervalSeconds = .1 });
        var visual = new EnemySeekVisual(945, 110, 30, 32, 480, 0);
        var selected = AutoFightSeek.SelectSeekDecision([visual], 1920, 1080);
        Assert.Equal(SeekCueKind.DirectionIndicator, selected.Cue);
        Assert.Equal(EnemyIndicatorDirection.Forward, selected.Direction);
        Assert.Equal(1, selected.SignalCount);
        Assert.Equal(visual, selected.Visual);
        var faultActive = false;
        CombatBattleObservation Forward(CaptureFrameStamp stamp)
        {
            var candidate = faultActive && scenario == "multiple"
                ? AutoFightSeek.SelectSeekDecision([visual, visual with { X = visual.X + 40 }], 1920, 1080) : selected;
            var passive = new BetterGenshinImpact.GameTask.AutoFight.Model.PassiveTargetObservation(false, false, clock.GetUtcNow().UtcDateTime,
                visual, 1920, 1080, candidate) { Source = stamp, BattleId = flow.Context.BattleId };
            Assert.True(AutoFightSeek.TryCreatePassiveDecision(passive, clock.GetUtcNow().UtcDateTime,
                out var admitted, out _, out _));
            Assert.Equal(EnemyIndicatorDirection.Forward, admitted.Direction);
            return new(stamp, faultActive && scenario == "wrong-source" ? Guid.NewGuid() : flow.Context.BattleId,
                CombatObservationQuality.Available, admitted, 1920, 1080)
            { Control = new(MotionStatus.Unknown, faultActive && scenario == "control-not-ready") };
        }
        for (var i = 0; i < 2000 && host.State != "Searching"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Searching", host.State);
        io.TargetFactory = Forward;
        for (var i = 0; i < 200 && host.State == "Searching"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Fighting", host.State);
        Assert.Equal("bounded-reengagement", host.Reason);
        io.TargetFactory = null;
        for (var i = 0; i < 3000 && host.State != "Searching"; i++)
            Assert.Equal(CombatBattleHostResult.Continue, await host.AdvanceAsync(flow, default));
        Assert.Equal("Searching", host.State);
        var toNoProgress = TimeSpan.FromSeconds(45.1) - clock.GetElapsedTime(started);
        if (toNoProgress > TimeSpan.Zero) clock.Advance(toNoProgress);
        Assert.Empty(io.Inputs.Where(input => input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset));
        faultActive = true;
        io.TargetFactory = Forward;
        if (scenario is "wrong-source" or "control-not-ready" or "multiple")
        {
            var rejected = CombatBattleHostResult.Continue;
            for (var i = 0; i < 1000 && rejected == CombatBattleHostResult.Continue; i++)
                rejected = await host.AdvanceAsync(flow, default);
            Assert.Equal(CombatBattleHostResult.Unconfirmed, rejected);
            Assert.DoesNotContain(io.Inputs, input => input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset or CombatBattleHostInputKind.Approach);
            return;
        }
        for (var i = 0; i < 10 && !io.Inputs.Any(input => input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset); i++)
        {
            var result = await host.AdvanceAsync(flow, default);
            if (result != CombatBattleHostResult.Continue) break;
        }
        Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
        Assert.True(io.Inputs.Any(input => input.Kind is CombatBattleHostInputKind.Camera or CombatBattleHostInputKind.CameraReset),
            $"state={host.State} reason={host.Reason} sentCamera={host.CameraRequests} scanUsed={io.Traces.Last().ScanUsed} approach={host.ApproachRequests}");
        Assert.Equal(1L, host.CameraRequests);
        Assert.Equal(1, io.Traces.Last().ScanUsed);
        if (scenario == "no-body")
        {
            var exhausted = CombatBattleHostResult.Continue;
            for (var i = 0; i < 10000 && exhausted == CombatBattleHostResult.Continue; i++)
                exhausted = await host.AdvanceAsync(flow, default);
            Assert.Equal(CombatBattleHostResult.Unconfirmed, exhausted);
            Assert.InRange(host.CameraRequests, 1, 24);
            Assert.Equal(24, io.Traces.Max(trace => trace.ScanUsed));
            Assert.DoesNotContain(io.Inputs, input => input.Kind == CombatBattleHostInputKind.Approach);
            return;
        }
        var resetCompleted = clock.GetTimestamp();
        io.TargetFactory = stamp =>
        {
            var health = new EnemySeekVisual(946, 400, 28, 4, 112);
            var passive = new BetterGenshinImpact.GameTask.AutoFight.Model.PassiveTargetObservation(true, false,
                clock.GetUtcNow().UtcDateTime, health, 1920, 1080) { Source = stamp, BattleId = flow.Context.BattleId };
            Assert.True(AutoFightSeek.TryCreatePassiveDecision(passive, clock.GetUtcNow().UtcDateTime, out var body, out _, out _));
            Assert.Equal(SeekCueKind.HealthBar, body.Cue);
            Assert.Equal(AutoFightSeekAction.ApproachVisibleEnemy, body.Action);
            return new(stamp, flow.Context.BattleId, CombatObservationQuality.Available, body, 1920, 1080)
            { Control = new(MotionStatus.Unknown, false) };
        };
        for (var i = 0; i < 1000 && host.ApproachRequests == 0; i++)
        {
            var result = await host.AdvanceAsync(flow, default);
            Assert.True(result == CombatBattleHostResult.Continue, $"reason={host.Reason} trace={io.Traces.Last()}");
            if (clock.GetElapsedTime(resetCompleted) < TimeSpan.FromMilliseconds(500)) Assert.Equal(0L, host.ApproachRequests);
        }
        Assert.Equal(1L, host.ApproachRequests);
        Assert.Equal(1L, host.CameraRequests);
        Assert.Equal(1, io.Traces.Last().ApproachUsed);
        Assert.InRange(clock.GetElapsedTime(resetCompleted).TotalMilliseconds, 500, 2000);
    }

    private static NativeCombatFlowRunner CreateFlow(bool json, ICombatFlowGame game, FakeTimeProvider clock)
    {
        if (json)
            return NativeCombatFlowRunner.Create(new JsonCombatStrategy
            {
                Actions = [new() { Character = "琴", Action = "attack(0.1,required)" }]
            }, game, clock: clock)!;
        return NativeCombatFlowRunner.Create(CombatFlowProgram.Compile("strategy(loop=battle)\n琴 attack(0.1,required)"), game, clock);
    }

    private sealed class ReturningGame(FakeTimeProvider clock) : ICombatFlowGame
    {
        public int Inputs { get; private set; }
        public object? Observe(string function, IReadOnlyList<object?> args, string actor) => true;
        public ValueTask<CombatFlowResult> ExecuteAsync(CombatFlowAction action, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            action.TryBeginInput();
            Inputs++;
            clock.Advance(TimeSpan.FromMilliseconds(100));
            return ValueTask.FromResult(CombatFlowResult.Succeeded);
        }
        public ValueTask YieldAsync(CancellationToken ct) => ValueTask.CompletedTask;
    }

    private sealed class ReplayIo(FakeTimeProvider clock, Guid battleId) : ICombatBattleHostIo
    {
        private readonly CaptureFrameSource _source = new(clock);
        public TimeProvider Clock => clock;
        public Guid BattleId => battleId;
        public List<CombatBattleHostInput> Inputs { get; } = [];
        public List<CombatBattleHostInput> Requests { get; } = [];
        public List<(CombatBattleHostInput Input,long Completed)> CompletedInputs { get; } = [];
        public List<CombatBattleHostTrace> Traces { get; } = [];
        public void Trace(CombatBattleHostTrace observation) => Traces.Add(observation);
        public int UnsentAttempts { get; set; }
        public CombatBattleHostInputResult? ForcedResult { get; init; }
        public Func<CombatBattleHostInput, CombatBattleHostInputResult>? ResultFactory { get; set; }
        public Func<CombatBattleHostInput, bool>? PrepareEvidence { get; set; }
        public Action? CancelEvidence { get; set; }
        public void CancelInputEvidence() => CancelEvidence?.Invoke();
        public bool PrepareInputEvidence(CombatBattleHostInput input, CancellationToken ct) => PrepareEvidence?.Invoke(input) ?? true;
        public bool PartyOpen { get; private set; }
        public bool RepeatSource { get; init; }
        public int SourcePeriodMilliseconds { get; init; }
        public int DelayCalls { get; private set; }
        public int ReleaseCount { get; private set; }
        public Func<CaptureFrameStamp, CombatBattleObservation>? TargetFactory { get; set; }
        public Action? AfterTargetCapture { get; set; }
        public Func<CaptureFrameStamp, PartySetupFinishObservation>? PartyFactory { get; set; }
        private CaptureFrameStamp _first;
        private CaptureFrameStamp _produced;
        public CombatBattleObservation ObserveTarget()
        {
            if (!_produced.IsKnown || clock.GetElapsedTime(_produced.CapturedTimestamp).TotalMilliseconds >= SourcePeriodMilliseconds)
                _produced = _source.Next();
            var stamp = _produced;
            if (!_first.IsKnown) _first = stamp;
            if (RepeatSource) stamp = _first;
            AfterTargetCapture?.Invoke();
            return TargetFactory?.Invoke(stamp) ?? new(stamp, battleId,
                CombatObservationQuality.Available, null, 1920, 1080);
        }
        public PartySetupFinishObservation ObservePartyBar()
        {
            var stamp = _source.Next();
            if (PartyFactory != null) return PartyFactory(stamp);
            return new(stamp.Sequence, stamp.CapturedAt, 1920, 1080, false, (ulong)stamp.Sequence)
            { Source = stamp };
        }
        public ValueTask<CombatBattleHostInputResult> SendAsync(CombatBattleHostInput input, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(input);
            if (ResultFactory != null) return ValueTask.FromResult(ResultFactory(input));
            if (ForcedResult is { } forced) return ValueTask.FromResult(forced);
            if (UnsentAttempts > 0)
            {
                UnsentAttempts--;
                clock.Advance(TimeSpan.FromMilliseconds(292));
                return ValueTask.FromResult(new CombatBattleHostInputResult(CombatBattleHostInputStatus.NotSent));
            }
            Inputs.Add(input);
            if (input.Kind == CombatBattleHostInputKind.OpenParty) PartyOpen = true;
            if (input.Kind == CombatBattleHostInputKind.CloseParty) PartyOpen = false;
            clock.Advance(TimeSpan.FromMilliseconds(10));
            var completed=clock.GetTimestamp();
            CompletedInputs.Add((input,completed));
            return ValueTask.FromResult(new CombatBattleHostInputResult(CombatBattleHostInputStatus.Sent, completed));
        }
        public ValueTask DelayAsync(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            DelayCalls++;
            clock.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return ValueTask.CompletedTask;
        }
        public void ReleaseInput() { PartyOpen = false; ReleaseCount++; }
    }
}
