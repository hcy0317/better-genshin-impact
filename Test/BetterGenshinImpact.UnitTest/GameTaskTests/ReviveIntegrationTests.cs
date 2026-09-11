using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Ui;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class ReviveIntegrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevivalMustReachStableOverworldBeforeStatue(bool ready)
    {
        var clock = new FakeTimeProvider();
        var driver = new Driver(clock, ready);
        var teleported = false;
        var work = UiRecovery.RecoverDefeatedAsync(driver, _ =>
        {
            Assert.True(driver.ReadyFrames >= 2);
            teleported = true;
            return Task.CompletedTask;
        }, default, clock:clock);
        if(ready) await work;
        else await Assert.ThrowsAsync<TimeoutException>(()=>work);
        Assert.Equal(ready,teleported);
        Assert.All(driver.Actions, action=>Assert.Equal(UiAction.ReviveParty,action));
    }

    [Fact]
    public async Task DomainDefeatEscapesPreActionsWithoutClearingTerminalState()
    {
        using var owner = TaskExecutionScope.BeginOwned();
        var defeated = new DomainDefeatedRetryException();
        Assert.Same(defeated,Assert.Throws<DomainDefeatedRetryException>(()=>
            TaskExecutionScope.RethrowCombatFailure(defeated,true,false,default)));
        TaskExecutionScope.ThrowIfFailed();
        var count=0;
        await Assert.ThrowsAsync<DomainDefeatedRetryException>(()=>AutoFightJsonTask.RunPreActionSequenceAsync(
            ["first","must-not-run"],_=>{count++;throw defeated;},()=>Task.CompletedTask,NullLogger.Instance,default));
        Assert.Equal(1,count);
        var terminal = new CombatNotFinishedException("not finished");
        TaskExecutionScope.Capture().Report(terminal);
        Assert.Same(terminal,Assert.Throws<CombatNotFinishedException>(()=>
            TaskExecutionScope.RethrowCombatFailure(defeated,true,false,default)));
    }

    private sealed class Driver(FakeTimeProvider clock,bool ready):IUiDriver
    {
        private long frame;
        public int ReadyFrames;
        public List<UiAction> Actions=[];
        public UiSnapshot Capture()
        {
            if(Actions.Count>0 && ready){ReadyFrames++;return new(++frame){MainHud=true};}
            return new(++frame){Revive=true,FullPartyDefeat=true};
        }
        public Task<bool> ActAsync(UiAction action,UiSnapshot observed,CancellationToken ct)
        {Actions.Add(action);return Task.FromResult(true);}
        public Task DelayAsync(int milliseconds,CancellationToken ct)
        {clock.Advance(TimeSpan.FromMilliseconds(milliseconds));ct.ThrowIfCancellationRequested();return Task.CompletedTask;}
    }
}
