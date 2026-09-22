using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using Fischless.GameCapture;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatSkillRecoveryInputTests
{
    private sealed class Trial : IDisposable
    {
        internal readonly FakeTimeProvider Clock = new();
        internal readonly CaptureFrameSource Source;
        internal readonly CombatFlowContext Context;
        internal readonly CombatSkillAttempts Attempts;
        internal readonly CombatFlowAction Original;
        internal readonly CombatFlowAction Confirmation;
        internal Trial(string fault = "", string actor = "班尼特", string syntax = "q(required)", double deadline = 8)
        {
            Source = new(Clock);
            Context = new(Clock);
            Attempts = new(Context.BattleId);
            var command = new CombatCommand(actor, syntax)
            { NativeSkillSequence = fault == "native-sequence" ? [new(actor, "keydown(E)"), new(actor, "wait(1)"), new(actor, "keyup(E)")] : null };
            Original = new(command, Context, () => true, deadline);
            CombatSkillInput.Send(Attempts, Original, actor, Source.Next(), (_, begin) =>
            {
                begin();
                var started = Clock.GetTimestamp();
                Clock.Advance(TimeSpan.FromMilliseconds(20));
                return new(fault == "unknown" ? CombatBattleHostInputStatus.Unknown : CombatBattleHostInputStatus.Sent,
                    fault == "missing-time" ? null : Clock.GetTimestamp(),
                    Error: fault == "error" ? new IOException("after native") : null)
                {
                    StartedTimestamp = fault == "missing-time" ? null : started,
                    NativeRequested = fault == "missing-count" ? null : fault == "zero" ? 0 : 2,
                    NativeSubmitted = fault == "partial" ? 1 : fault == "zero" ? 0 : 2
                };
            }, default, Clock);
            Confirmation = new(command, Context, () => true, deadline, confirmationAttempt: Original.PendingAttempt);
        }
        internal CombatSkillObservation Ready(int advanceMs = 210)
        {
            Clock.Advance(TimeSpan.FromMilliseconds(advanceMs));
            return new CombatSkillObservation(Context.BattleId, 0, Context.Now, false, true).WithSource(Source.Next(), Clock);
        }
        internal CombatSkillRecoveryPulse? Claim(CombatSkillObservation sample, bool controlled = true) =>
            Attempts.TryClaimRecovery(Confirmation, sample, controlled);
        public void Dispose() { Attempts.Dispose(); Context.Dispose(); }
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("partial")]
    [InlineData("zero")]
    [InlineData("missing-count")]
    [InlineData("missing-time")]
    [InlineData("error")]
    public void RecoveryRequiresAnExactCompleteOriginalReceipt(string fault)
    {
        using var trial = new Trial(fault);
        Assert.Null(trial.Claim(trial.Ready()));
        Assert.Null(trial.Claim(trial.Ready()));
    }

    [Theory]
    [InlineData("迪希雅", "e(required)")]
    [InlineData("迪卢克", "e(required)")]
    [InlineData("纳维娅", "e(required)")]
    [InlineData("砂糖", "e(required)")]
    [InlineData("琴", "e(required)")]
    [InlineData("钟离", "e(required)")]
    public void RecoveryIsClosedOutsideTheExplicitSingleCastAllowlist(string actor, string syntax)
    {
        using var trial = new Trial(actor: actor, syntax: syntax);
        Assert.Null(trial.Claim(trial.Ready()));
        Assert.Null(trial.Claim(trial.Ready()));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("control")]
    [InlineData("repeat")]
    [InlineData("foreign-session")]
    [InlineData("stale")]
    public void AnInvalidObservationClearsTheContinuousReadyPair(string fault)
    {
        using var trial = new Trial();
        var first = trial.Ready(100);
        Assert.Null(trial.Claim(first));
        var second = trial.Ready(210);
        second = fault switch
        {
            "unknown" => second with { Ready = null },
            "repeat" => first,
            "foreign-session" => second.WithSource(new CaptureFrameSource(trial.Clock).Next(), trial.Clock),
            "stale" => second.WithSource(first.SourceStamp, trial.Clock),
            _ => second
        };
        Assert.Null(trial.Claim(second, fault != "control"));
        Assert.Null(trial.Claim(trial.Ready(10)));
        Assert.NotNull(trial.Claim(trial.Ready(210)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryCannotStartAfterItsWindowOrWithInsufficientOriginalBudget(bool insufficient)
    {
        using var trial = new Trial(deadline: insufficient ? 2.2 : 8);
        Assert.Null(trial.Claim(trial.Ready(100)));
        Assert.Null(trial.Claim(trial.Ready(insufficient ? 210 : 1000)));
    }

    [Fact]
    public void ARealCooldownPermanentlyRevokesRecoveryEvenIfReadyReturns()
    {
        using var trial = new Trial();
        Assert.Null(trial.Claim(trial.Ready(100)));
        var cooling = trial.Ready(100) with { CoolingDown = true, Ready = false };
        Assert.True(trial.Attempts.Observe("班尼特", Method.Burst, cooling));
        Assert.Null(trial.Claim(trial.Ready(210)));
    }

    [Fact]
    public void NativeSkillSequenceIsNotEligibleEvenForTheWhitelistedActor()
    {
        using var trial = new Trial("native-sequence", "钟离", "e(hold,required)");
        Assert.Null(trial.Claim(trial.Ready()));
        Assert.Null(trial.Claim(trial.Ready()));
    }

    [Fact]
    public void RecoveryAllowanceCanBeClaimedOnlyOnceUnderConcurrentObservation()
    {
        using var trial = new Trial();
        Assert.Null(trial.Claim(trial.Ready(100)));
        var sample = trial.Ready();
        var pulses = new System.Collections.Concurrent.ConcurrentBag<CombatSkillRecoveryPulse>();
        Parallel.For(0, 16, _ => { if (trial.Claim(sample) is { } pulse) pulses.Add(pulse); });
        Assert.Single(pulses);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AdmissionIsCheckedAgainAtTheActualSecondNativeBoundary(bool cancel)
    {
        using var trial = new Trial();
        using var cancellation = new CancellationTokenSource();
        Assert.Null(trial.Claim(trial.Ready(100)));
        var pulse = Assert.IsType<CombatSkillRecoveryPulse>(trial.Claim(trial.Ready()));
        var nativeSent = false;
        Assert.ThrowsAny<Exception>(() => CombatSkillInput.SendRecovery(trial.Attempts, trial.Confirmation, pulse,
            (_, begin) =>
            {
                if (cancel) cancellation.Cancel();
                else trial.Clock.Advance(TimeSpan.FromSeconds(1));
                begin();
                nativeSent = true;
                return new(CombatBattleHostInputStatus.Sent, trial.Clock.GetTimestamp());
            }, cancellation.Token, trial.Clock));
        Assert.False(nativeSent);
        Assert.Equal(1, trial.Context.InputAttemptRevision);
        Assert.Null(trial.Claim(trial.Ready(10)));
    }

    [Fact]
    public void RecoveryTokenIsSingleUseEvenWhenTheSecondInputWasNotSent()
    {
        using var trial = new Trial();
        Assert.Null(trial.Claim(trial.Ready(100)));
        var pulse = Assert.IsType<CombatSkillRecoveryPulse>(trial.Claim(trial.Ready()));
        var calls = 0;
        CombatBattleHostInputResult NoSend(CombatNativeInputRequest _, Action __)
        { calls++; return new(CombatBattleHostInputStatus.NotSent); }
        CombatSkillInput.SendRecovery(trial.Attempts, trial.Confirmation, pulse, NoSend, default, trial.Clock);
        CombatSkillInput.SendRecovery(trial.Attempts, trial.Confirmation, pulse, NoSend, default, trial.Clock);
        Assert.Equal(1, calls);
        Assert.Null(trial.Claim(trial.Ready()));
        Assert.Equal(trial.Original.PendingAttempt, trial.Attempts.GetAttempt("班尼特", Method.Burst));
        Assert.Equal(1, trial.Context.InputAttemptRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecoveryKeepsAttemptAndDeadlineButMovesTheConfirmationFence(bool partial)
    {
        using var trial = new Trial();
        Assert.Null(trial.Claim(trial.Ready(100)));
        var pulse = Assert.IsType<CombatSkillRecoveryPulse>(trial.Claim(trial.Ready()));
        var oldFrame = trial.Ready(1) with { CoolingDown = true, Ready = false };
        Guid secondRequest = default;
        CombatSkillInput.SendRecovery(trial.Attempts, trial.Confirmation, pulse, (request, begin) =>
        {
            secondRequest = request.Id;
            begin();
            trial.Clock.Advance(TimeSpan.FromMilliseconds(50));
            return new(partial ? CombatBattleHostInputStatus.Unknown : CombatBattleHostInputStatus.Sent,
                trial.Clock.GetTimestamp()) { NativeRequested = 2, NativeSubmitted = partial ? 1 : 2 };
        }, default, trial.Clock);
        Assert.NotEqual(trial.Original.InputRequestId, secondRequest);
        Assert.Equal(2, trial.Context.InputAttemptRevision);
        Assert.Equal(trial.Original.PendingAttempt, trial.Attempts.GetAttempt("班尼特", Method.Burst));
        Assert.False(trial.Attempts.Observe("班尼特", Method.Burst, oldFrame));
        var postFrame = trial.Ready(1) with { CoolingDown = true, Ready = false };
        Assert.True(trial.Attempts.Observe("班尼特", Method.Burst, postFrame));
        var receipt = trial.Attempts.TakeConfirmation("班尼特", Method.Burst, trial.Original.CommandId, trial.Context.Now);
        Assert.Equal(trial.Original.PendingAttempt, receipt);
        Assert.Equal(8, receipt!.Deadline);
        Assert.Equal(0, receipt.InputAt);
        Assert.Null(trial.Attempts.TakeConfirmation("班尼特", Method.Burst, trial.Original.CommandId, trial.Context.Now));
    }
}
