using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoFight.Script.Flow;
using BetterGenshinImpact.GameTask.AutoFight;
using Fischless.GameCapture;
using Fischless.WindowsInput;
using Microsoft.Extensions.Time.Testing;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoFightTests;

public class CombatSkillInputTests
{
    [Fact]
    public async Task FailureWindowRetainsOriginalReceiptWithoutBeforeImageOrConfirmingTheSkill()
    {
        var saved = new List<BetterGenshinImpact.GameTask.Common.DiagnosticEvidence>();
        await using var evidence = new BetterGenshinImpact.GameTask.Common.DiagnosticEvidenceScope((item, _) =>
        { saved.Add(item); return Task.CompletedTask; });
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        using var context = new CombatFlowContext(clock);
        using var attempts = new CombatSkillAttempts(context.BattleId);
        var action = new CombatFlowAction(new CombatCommand("琴", "e(required)"), context, () => true, 8);
        var before = producer.Next();
        Guid requestId = default;
        CombatSkillInput.Send(attempts, action, "琴", before, (request, begin) =>
        {
            requestId = request.Id;
            begin();
            var started = clock.GetTimestamp();
            clock.Advance(TimeSpan.FromMilliseconds(30));
            return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp())
            { StartedTimestamp = started, NativeRequested = 2, NativeSubmitted = 2 };
        }, default, clock);
        var attempt = action.PendingAttempt!;
        var fields = attempts.InputEvidence(attempt.AttemptId);
        Assert.Contains(requestId.ToString(), fields["skill:attempt"]);
        Assert.Contains($"{before.SessionId}/{before.Sequence}", fields["skill:originalSource"]);
        Assert.Contains("nativeSubmitted=2", fields["skill:originalReceipt"]);
        Assert.Equal(CombatSkillAttemptState.Pending, attempts.GetState("琴", Method.Skill, context.Now));
        Assert.Null(attempts.TakeConfirmation("琴", Method.Skill, action.CommandId, context.Now));
        using var frame = new BetterGenshinImpact.GameTask.Model.Area.ImageRegion(
            new OpenCvSharp.Mat(2, 2, OpenCvSharp.MatType.CV_8UC3, OpenCvSharp.Scalar.Black), 0, 0)
        { FrameStamp = producer.Next() };
        evidence.RequestWindowFromFrame(attempt.AttemptId.ToString(), "skill-unconfirmed", frame, "no before image", fields: fields);
        Assert.Null(attempts.TakeConfirmation("琴", Method.Skill, action.CommandId, context.Now));
        await evidence.DisposeAsync();
        Assert.Equal(fields["skill:originalReceipt"], Assert.Single(saved).Fields!["skill:originalReceipt"]);
        Assert.Equal(1, context.InputAttemptRevision);
    }

    [Fact]
    public void NotSentDoesNotCreateAPendingSkillAndKeepsTheOriginalActionDeadline()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        using var context = new CombatFlowContext(clock);
        using var attempts = new CombatSkillAttempts(context.BattleId);
        var action = new CombatFlowAction(new CombatCommand("琴", "e(required)"), context, () => true, 8);
        var result = CombatSkillInput.Send(attempts, action, "琴", producer.Next(),
            (_, _) => new(CombatBattleHostInputStatus.NotSent), default, clock);
        Assert.Equal(CombatFlowResult.AwaitingObservation, result);
        Assert.False(attempts.IsOccupied("琴", Method.Skill));
        Assert.Null(action.InputAt);
        Assert.Equal(0, context.InputAttemptRevision);
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(CombatFlowResult.Pending, CombatSkillInput.Send(attempts, action, "琴", producer.Next(),
            (_, begin) => { begin(); return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()); }, default, clock));
        Assert.Equal(2, action.InputAt);
        Assert.Equal(8, action.PendingAttempt!.Deadline);
        Assert.Equal(1, context.InputAttemptRevision);
    }

    [Fact]
    public void AConfirmedZeroSubmissionCanRemoveOnlyTheSpeculativeUnsubmittedSlot()
    {
        var clock = new FakeTimeProvider();
        using var context = new CombatFlowContext(clock);
        using var attempts = new CombatSkillAttempts(context.BattleId);
        var action = new CombatFlowAction(new CombatCommand("琴", "e(required)"), context, () => true, 8);
        var rejected = new InputDispatchException("zero native input");
        Assert.Same(rejected, Assert.Throws<InputDispatchException>(() => CombatSkillInput.Send(attempts, action, "琴",
            new CaptureFrameSource(clock).Next(), (_, begin) =>
            {
                begin();
                return new(CombatBattleHostInputStatus.Failed, Error: rejected) { NativeRequested = 2, NativeSubmitted = 0 };
            }, default, clock)));
        Assert.Null(action.InputAt);
        Assert.False(attempts.IsOccupied("琴", Method.Skill));
        Assert.Equal(0, context.InputAttemptRevision);
    }

    [Fact]
    public void APartialSubmissionKeepsItsPendingSlotAndCannotBeResent()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        using var context = new CombatFlowContext(clock);
        using var attempts = new CombatSkillAttempts(context.BattleId);
        var command = new CombatCommand("琴", "e(required)");
        var action = new CombatFlowAction(command, context, () => true, 8);
        Assert.Equal(CombatFlowResult.Pending, CombatSkillInput.Send(attempts, action, "琴", producer.Next(),
            (_, begin) => { begin(); return new(CombatBattleHostInputStatus.Unknown) { NativeRequested = 2, NativeSubmitted = 1, ObservableAfterTimestamp = clock.GetTimestamp() }; }, default, clock));
        Assert.Equal(CombatFlowResult.Pending, CombatSkillInput.Send(attempts,
            new CombatFlowAction(command, context, () => true, 8), "琴", producer.Next(),
            (_, _) => throw new InvalidOperationException("must not replay"), default, clock));
        Assert.True(attempts.IsOccupied("琴", Method.Skill));
        Assert.Equal(1, context.InputAttemptRevision);
    }

    [Fact]
    public void SendingInputYieldsPendingAndOnlyANewCooldownFrameCanConfirmIt()
    {
        var clock = new FakeTimeProvider();
        var producer = new CaptureFrameSource(clock);
        using var context = new CombatFlowContext(clock);
        using var attempts = new CombatSkillAttempts(context.BattleId);
        var command = new CombatCommand("琴", "e(required)");
        var action = new CombatFlowAction(command, context, () => true, 8);
        var sent = 0;
        Assert.Equal(CombatFlowResult.Pending, CombatSkillInput.Send(attempts, action, "琴", producer.Next(),
            (_, begin) => { begin(); sent++; clock.Advance(TimeSpan.FromMilliseconds(100)); return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()); }, default, clock));
        var next = new CombatFlowAction(command, context, () => true, 8);
        Assert.Equal(CombatFlowResult.Pending, CombatSkillInput.Send(attempts, next, "琴", producer.Next(),
            (_, begin) => { begin(); sent++; return new(CombatBattleHostInputStatus.Sent, clock.GetTimestamp()); }, default, clock));
        Assert.Equal(1, sent);
        Assert.Null(attempts.TakeConfirmation("琴", Method.Skill, commandId: action.CommandId, now: context.Now));
        clock.Advance(TimeSpan.FromMilliseconds(400));
        Assert.True(attempts.Observe("琴", Method.Skill,
            new CombatSkillObservation(context.BattleId, 0, context.Now, true, false).WithSource(producer.Next(), clock)));
        var receipt = attempts.TakeConfirmation("琴", Method.Skill, action.CommandId, context.Now);
        Assert.NotNull(receipt);
        Assert.Equal(0, receipt.InputAt);
        Assert.Equal(8, receipt.Deadline);
    }
}
