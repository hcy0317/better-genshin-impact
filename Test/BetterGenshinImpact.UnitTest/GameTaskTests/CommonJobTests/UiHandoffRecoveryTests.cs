using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.Common.Ui;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.BgiVision;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonJobTests;

public class UiHandoffRecoveryTests
{
    [Fact]
    public async Task OrdinaryLowHpRecoversBeforeHandoffWithoutChangingFailedOutcome()
    {
        var driver = new Replay { Ordinary = true, LowHp = true };
        using var parent = UiOperation.Begin("caller", TimeSpan.FromSeconds(5), clock: driver.Time);
        var calls = 0;
        var result = await ScriptStepOutcomeRunner.RunAsync(
            () => Task.FromResult(new ScriptExecutionResult(ScriptOutcomeKind.Failed, "unsafe route replay")),
            _ => UiHandoffRecovery.RecoverAsync(driver, _ =>
            {
                calls++;
                driver.LowHp = false;
                driver.OrdinaryFrames = 0;
                return Task.CompletedTask;
            }, default, driver.Time), default, recoveryBudget: UiHandoffRecovery.Budget);
        Assert.Equal(1, calls);
        Assert.True(driver.OrdinaryFrames >= 2);
        Assert.Equal(ScriptOutcomeKind.Failed, result.Outcome.Kind);
    }

    [Fact]
    public async Task FailedScriptNeedsStatueAndFreshOrdinaryFramesBeforeNextTask()
    {
        var driver = new Replay();
        var recovered = 0;
        var result = await ScriptStepOutcomeRunner.RunAsync(
            () => Task.FromResult(new ScriptExecutionResult(ScriptOutcomeKind.Failed, "macro failed")),
            _ => UiHandoffRecovery.RecoverAsync(driver, token =>
            {
                token.ThrowIfCancellationRequested();
                recovered++;
                driver.Ordinary = true;
                return Task.CompletedTask;
            }, default, driver.Time), default);
        Assert.Equal(1, recovered);
        Assert.True(driver.OrdinaryFrames >= 2);
        Assert.Equal(ScriptOutcomeKind.Failed, result.Outcome.Kind);
    }

    [Theory]
    [InlineData(25, 120, true)]
    [InlineData(90, 120, false)]
    [InlineData(25, 10, false)]
    public async Task ProductionRecoveryBudgetKeepsItsOuterDeadline(double duration, double parentBudget, bool succeeds)
    {
        var driver = new Replay();
        using var parent = UiOperation.Begin("caller", TimeSpan.FromSeconds(parentBudget), clock: driver.Time);
        var calls = 0;
        ScriptStepOutcome? result = null;
        var error = await Record.ExceptionAsync(async () => result = await ScriptStepOutcomeRunner.RunAsync(
            () => Task.FromResult(new ScriptExecutionResult(ScriptOutcomeKind.Failed, "original")),
            _ => UiHandoffRecovery.RecoverAsync(driver, token =>
            {
                calls++;
                driver.Time.Advance(TimeSpan.FromSeconds(duration));
                driver.Ordinary = true;
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }, default, driver.Time), default, recoveryBudget: UiHandoffRecovery.Budget));
        Assert.Equal(1, calls);
        if (succeeds)
        {
            Assert.Null(error);
            Assert.Equal(ScriptOutcomeKind.Failed, result!.Value.Outcome.Kind);
            Assert.True(driver.OrdinaryFrames >= 2);
        }
        else { Assert.IsType<TaskFailureRecoveryException>(error); Assert.Null(result); }
    }

    [Fact]
    public async Task OtherCallersKeepTheirTwentySecondRecoveryDefault()
    {
        var clock = new FakeTimeProvider();
        using var parent = UiOperation.Begin("caller", TimeSpan.FromSeconds(120), clock: clock);
        await Assert.ThrowsAsync<TaskFailureRecoveryException>(() => ScriptStepOutcomeRunner.RunAsync(
            () => Task.FromResult(new ScriptExecutionResult(ScriptOutcomeKind.Failed, "original")),
            _ => { clock.Advance(TimeSpan.FromSeconds(25)); return Task.CompletedTask; }, default));
    }

    [Fact]
    public async Task OrdinaryWorldNeverTriggersUnnecessaryTeleport()
    {
        var driver = new Replay { Ordinary = true };
        await UiHandoffRecovery.RecoverAsync(driver, _ => throw new Exception("must not teleport"), default, driver.Time);
        Assert.True(driver.OrdinaryFrames >= 4);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransformationNeverOverridesClimbingOrBreakoutVeto(bool climbing)
    {
        var driver = new Replay { Climbing = climbing, Breakout = !climbing };
        using var parent = UiOperation.Begin("caller", TimeSpan.FromSeconds(3), clock: driver.Time);
        var calls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => UiHandoffRecovery.RecoverAsync(driver,
            _ => { calls++; return Task.CompletedTask; }, default, driver.Time));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExplicitFlightCanRecoverWithoutGuessingAnUnknownTransformation()
    {
        var driver = new Replay { Ordinary = true, UnknownIdentity = true, Flying = true };
        var calls = 0;
        await UiHandoffRecovery.RecoverAsync(driver, _ =>
        {
            calls++;
            driver.UnknownIdentity = driver.Flying = false;
            return Task.CompletedTask;
        }, default, driver.Time);
        Assert.Equal(1, calls);
        Assert.True(driver.OrdinaryFrames >= 2);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("control-unobserved")]
    [InlineData("climb")]
    [InlineData("party-rejected")]
    [InlineData("old-source")]
    public async Task IncompleteWorldEvidenceDoesNotAuthorizeTeleportOrHandoff(string failure)
    {
        var driver = new Replay { Ordinary = true, UnknownIdentity = failure == "unknown",
            ControlMissing = failure == "control-unobserved", Climbing = failure == "climb",
            Rejected = failure == "party-rejected", LowHp = true, RepeatFrame = failure == "old-source" };
        using var parent = UiOperation.Begin("caller", TimeSpan.FromSeconds(3), clock: driver.Time);
        var calls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => UiHandoffRecovery.RecoverAsync(driver,
            _ => { calls++; return Task.CompletedTask; }, default, driver.Time));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData("still-transformed")]
    [InlineData("still-flying")]
    [InlineData("old-frame")]
    [InlineData("changed-source")]
    [InlineData("map-failed")]
    [InlineData("still-low-hp")]
    public async Task RecoveryCallbackReturningIsNotProofOfHandoff(string failure)
    {
        var driver = new Replay();
        using var parent = UiOperation.Begin("caller", TimeSpan.FromSeconds(4), clock: driver.Time);
        var calls = 0;
        var error = await Record.ExceptionAsync(() => UiHandoffRecovery.RecoverAsync(driver, _ =>
        {
            calls++;
            if (failure == "map-failed") throw new InvalidOperationException("map not confirmed");
            driver.Ordinary = failure != "still-transformed";
            driver.Flying = failure == "still-flying";
            driver.LowHp = failure == "still-low-hp";
            driver.RepeatFrame = failure == "old-frame";
            if (failure == "changed-source") driver.ChangeSource();
            return Task.CompletedTask;
        }, default, driver.Time));
        Assert.NotNull(error);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LowHpCannotOverrideControlOrBreakout(bool breakout)
    {
        var driver = new Replay { Ordinary = true, LowHp = true, Controlled = true, Breakout = breakout };
        using var parent = UiOperation.Begin("caller", TimeSpan.FromSeconds(3), clock: driver.Time);
        var calls = 0;
        await Assert.ThrowsAsync<TimeoutException>(() => UiHandoffRecovery.RecoverAsync(driver,
            _ => { calls++; return Task.CompletedTask; }, default, driver.Time));
        Assert.Equal(0, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndUnconfirmedCombatNeverAuthorizeRecovery(bool combat)
    {
        using var scope = TaskExecutionScope.BeginOwned();
        using var cancellation = new CancellationTokenSource();
        if (combat) Assert.Throws<CombatNotFinishedException>(() => TaskExecutionScope.StopUnconfirmedCombat("still fighting"));
        else cancellation.Cancel();
        var driver = new Replay();
        var calls = 0;
        Assert.NotNull(await Record.ExceptionAsync(() => UiHandoffRecovery.RecoverAsync(driver,
            _ => { calls++; return Task.CompletedTask; }, cancellation.Token, driver.Time)));
        Assert.Equal(0, calls);
    }

    private sealed class Replay : IUiDriver
    {
        internal FakeTimeProvider Time { get; } = new();
        private CaptureFrameSource _source;
        private CaptureFrameStamp _last;
        internal bool Ordinary;
        internal bool UnknownIdentity, ControlMissing, Climbing, Flying, Rejected, LowHp, RepeatFrame, Breakout, Controlled;
        internal int OrdinaryFrames;
        public Replay() => _source = new(Time);
        internal void ChangeSource() => _source = new(Time);
        public UiSnapshot Capture()
        {
            Time.Advance(TimeSpan.FromMilliseconds(1));
            if (Ordinary) OrdinaryFrames++;
            if (!RepeatFrame || !_last.IsKnown) _last = _source.Next();
            return new UiSnapshot(1) { MainHud = true, World = new(Climbing || Flying || Breakout || Controlled, LowHp, Rejected)
                { OrdinaryAvatarHud = Ordinary && !UnknownIdentity, Transformed = !Ordinary && !UnknownIdentity,
                    ControlObserved = !ControlMissing, KeyboardBreakout = Breakout,
                    Motion = Climbing ? MotionStatus.Climb : Flying ? MotionStatus.Fly : MotionStatus.Unknown } }
                .WithSource(_last, Time, UiSnapshot.RecoveryMaximumAge);
        }
        public Task DelayAsync(int milliseconds, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Time.Advance(TimeSpan.FromMilliseconds(milliseconds));
            return Task.CompletedTask;
        }
        public Task<bool> ActAsync(UiAction action, UiSnapshot observed, CancellationToken ct)
            => throw new InvalidOperationException("No UI input expected in the recorded handoff");
    }
}
